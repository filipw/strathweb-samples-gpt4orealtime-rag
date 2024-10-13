using Azure.AI.OpenAI;
using OpenAI.RealtimeConversation;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;

var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ??
               throw new Exception("'AZURE_OPENAI_ENDPOINT' must be set");
var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ??
          throw new Exception("'AZURE_OPENAI_ENDPOINT' must be set");
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-realtime-preview";

var aoaiClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(key));
var client = aoaiClient.GetRealtimeConversationClient(deploymentName);

var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT") ??
                     throw new Exception("'AZURE_SEARCH_ENDPOINT' must be set");

var searchCredential = Environment.GetEnvironmentVariable("AZURE_SEARCH_API_KEY") ??
                       throw new Exception("'AZURE_SEARCH_API_KEY' must be set");
var indexName = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX");
var indexClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchCredential));
var searchClient = indexClient.GetSearchClient(indexName);

using var session = await client.StartConversationSessionAsync();

var sessionOptions = new ConversationSessionOptions()
{
    Instructions = """
                   You are a helpful voice-enabled customer assistant for a sports store.
                   As the voice assistant, you answer questions very succinctly and friendly. Do not enumerate any items and be brief.
                   Only answer questions based on information available in the product search, accessible via the 'search' tool.
                   Always use the 'search' tool before answering a question about products.
                   If the 'search' tool does not yield any product results, respond that you are unable to answer the given question.
                   """,
    Tools =
    {
        new ConversationFunctionTool
        {
            Name = "search",
            Description = "Search the product catalog for product information",
            Parameters = BinaryData.FromString("""
                                               {
                                                 "type": "object",
                                                 "properties": {
                                                   "query": {
                                                     "type": "string",
                                                     "description": "The search query e.g. 'miami themed products'"
                                                   }
                                                 },
                                                 "required": ["query"]
                                               }
                                               """)
        }
    },
    InputAudioFormat = ConversationAudioFormat.Pcm16,
    OutputAudioFormat = ConversationAudioFormat.Pcm16,
    Temperature = 0
};

await session.ConfigureSessionAsync(sessionOptions);

var inputAudioPath = Path.Combine(Directory.GetCurrentDirectory(), "user-question.pcm");
await using var inputAudioStream = File.OpenRead(inputAudioPath);
await session.SendAudioAsync(inputAudioStream);

var functionCalls = new Dictionary<string, StringBuilder>();
await Process(session);

async Task Process(RealtimeConversationSession session)
{
    await using var outputAudioStream = File.Create("assistant-response.pcm");
    await foreach (var update in session.ReceiveUpdatesAsync())
    {
        switch (update)
        {
            // collecting function arguments
            case ConversationFunctionCallArgumentsDeltaUpdate argumentsDeltaUpdate:
                if (!functionCalls.TryGetValue(argumentsDeltaUpdate.CallId, out StringBuilder value))
                {
                    value = new StringBuilder();
                    functionCalls[argumentsDeltaUpdate.CallId] = value;
                }

                value.Append(argumentsDeltaUpdate.Delta);
                break;
            // collecting audio chunks for playback
            case ConversationAudioDeltaUpdate audioDeltaUpdate:
                outputAudioStream.Write(audioDeltaUpdate.Delta?.ToArray() ?? []);
                break;
            // collecting assistant response transcript to display in console
            case ConversationOutputTranscriptionDeltaUpdate outputTranscriptDeltaUpdate:
                Console.Write(outputTranscriptDeltaUpdate.Delta);
                break;
            // indicates assistant item streaming finished
            case ConversationItemFinishedUpdate itemFinishedUpdate:
            {
                // if we have function call, we should invoke it and send back to the session
                if (itemFinishedUpdate.FunctionCallId is not null &&
                    functionCalls.TryGetValue(itemFinishedUpdate.FunctionCallId, out var functionCallArgs))
                {
                    var arguments = functionCallArgs.ToString();
                    Console.WriteLine($" -> Invoking: {itemFinishedUpdate.FunctionName}({arguments})");
                    var functionResult = await InvokeFunction(itemFinishedUpdate.FunctionName, arguments);
                    functionCalls[itemFinishedUpdate.FunctionCallId] = new StringBuilder();
                    if (functionResult != "")
                    {
                        var functionOutputItem =
                            ConversationItem.CreateFunctionCallOutput(callId: itemFinishedUpdate.FunctionCallId,
                                output: functionResult);
                        await session.AddItemAsync(functionOutputItem);
                    }
                }

                break;
            }
            // assistant turn ended
            case ConversationResponseFinishedUpdate turnFinishedUpdate:
                // if we invoked a function, we skip the user turn
                if (turnFinishedUpdate.CreatedItems.Any(item => item.FunctionCallId is not null))
                {
                    Console.WriteLine($" -> Short circuit the client turn due to function invocation");
                    await session.StartResponseTurnAsync();
                }
                else
                {
                    return;
                }

                break;
            case ConversationErrorUpdate conversationErrorUpdate:
                Console.Error.WriteLine($"Error! {conversationErrorUpdate}");
                return;
        }
    }
}

async Task<string> InvokeFunction(string functionName, string functionArguments)
{
    if (functionName == "search")
    {
        var doc = JsonDocument.Parse(functionArguments);
        var root = doc.RootElement;

        var query = root.GetProperty("query").GetString();

        var result = await InvokeSearch(query, searchClient);
        return result;
    }

    throw new Exception($"Unsupported tool '{functionName}'");
}

static async Task<string> InvokeSearch(string query, SearchClient searchClient)
{
    SearchResults<Product> response = await searchClient.SearchAsync<Product>(query, new SearchOptions
    {
        Size = 5
    });
    var results = new StringBuilder();
    var resultCount = 0;
    await foreach (var result in response.GetResultsAsync())
    {
        resultCount++;
        results.AppendLine($"Product: {result.Document.Name}, Description: {result.Document.Description}");
    }

    results.AppendLine($"Total results: {resultCount}");

    var documentation = results.ToString();
    Console.WriteLine($" -> Retrieved documentation:\n{documentation}");
    return documentation;
}

public record Product(
    [property: JsonPropertyName("description")]
    string Description,
    [property: JsonPropertyName("name")] string Name);