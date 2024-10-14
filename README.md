# Sample RAG pattern app with real-time voice GPT-4o model

The demo works with Azure OpenAI and Azure AI Search.

## Prerequisites

### Variables

You need to create an Azure OpenAI resource, GPT-4o Realtime deployment and Azure AI Search resource.

```bash
AZURE_OPENAI_ENDPOINT=wss://<YOUR RESOURCE NAME>.openai.azure.com
AZURE_OPENAI_DEPLOYMENT=<YOUR DEPLOYMENT NAME>
AZURE_OPENAI_API_KEY=<YOUR AZURE OPENAI KEY>
AZURE_SEARCH_ENDPOINT=https://<YOUR AI SEARCH ACCOUNT>.windows.net
AZURE_SEARCH_INDEX=<YOUR SEARCH INDEX NAME>
AZURE_SEARCH_API_KEY=<YOUR AI SEARCH KEY>
```

### Index the data

Additionally, make sure the search is configured appropriately. You can import the index using the attached [configuration file](./resources/search_config.json). 
Next, you need to index the data from the [sample data folder](./resources/sample_data/) into the new index. There are a few ways to do this, the simplest is to put the files in a blob storage, and then tell AI Search to index using JSON mode.

## Running the demo

```bash
cd Strathweb.Samples.Realtime.Rag
dotnet run
```

For portability (.NET audio APIs and mic control are not cross-platform), the demo uses raw PCM audio files for input and output. The audio input comes from the attached sample audio file `user-question.pcm` (which is the user asking "do you have any flamingo products?"). The output is streamed back from the GPT-4o Realtime model as audio and saved into `assistant-response.pcm`. The response will be based on the data "flamingo" indexed in the Azure AI Search index.

The files can be played back using any audio player that supports raw PCM audio. It can also be played back from the command line using [ffplay](https://www.ffmpeg.org/ffplay.html):

```bash
ffplay -f s16le -ar 24000 assistant-response.pcm
```