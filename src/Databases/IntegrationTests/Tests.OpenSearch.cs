﻿using LangChain.Databases.OpenSearch;
using LangChain.Providers;
using LangChain.Providers.Amazon.Bedrock;
using LangChain.Providers.Amazon.Bedrock.Predefined.Amazon;
using LangChain.Providers.Amazon.Bedrock.Predefined.Anthropic;
using LangChain.Sources;
using static LangChain.Chains.Chain;

namespace LangChain.Databases.IntegrationTests;

//
// docker run -p 9200:9200 -p 9600:9600 -e "discovery.type=single-node" -e "plugins.security.disabled=true" -e "OPENSEARCH_INITIAL_ADMIN_PASSWORD=<custom-admin-password>" opensearchproject/opensearch:latest
//
public partial class Tests
{
    private string? _indexName;
    private OpenSearchVectorDatabaseOptions? _options;
    private OpenSearchVectorDatabase? _vectorDatabase;
    private BedrockProvider? _provider;
    private IEmbeddingModel? _embeddingModel;
    private int _dimensions = 1536;

    #region Query Images

    public void setup_image_tests()
    {
        _indexName = "images-index";
        var username = Environment.GetEnvironmentVariable("OPENSEARCH_USERNAME");
        var endpoint = Environment.GetEnvironmentVariable("OPENSEARCH_URI");
        var uri = new Uri(endpoint!);
        //var uri = new Uri("http://localhost:9200");
        var password = Environment.GetEnvironmentVariable("OPENSEARCH_INITIAL_ADMIN_PASSWORD");
        _options = new OpenSearchVectorDatabaseOptions
        {
            ConnectionUri = uri,
            Username = username,
            Password = password,
        };
        _dimensions = 1024;

        _provider = new BedrockProvider();
        _embeddingModel = new TitanEmbedImageV1Model(_provider)
        {
            Settings = new BedrockEmbeddingSettings
            {
                Dimensions = _dimensions
            }
        };
        _vectorDatabase = new OpenSearchVectorDatabase(_options);
    }

    [Test]
    [Explicit]
    public async Task index_test_images()
    {
        setup_image_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);

        string[] extensions = { ".bmp",".gif", ".jpg", ".jpeg", ".png", ".tiff" };
        var files = Directory.EnumerateFiles(@"[images directory]", "*.*", SearchOption.AllDirectories)
            .Where(s => extensions.Any(ext => ext == Path.GetExtension(s)));

        var images = files.ToBinaryData();

        var documents = new List<Document>();

        foreach (BinaryData image in images)
        {
            var model = new Claude3HaikuModel(_provider!);
            var message = new Message(" \"what's this an image of and describe the details?\"", MessageRole.Human);

            var chatRequest = ChatRequest.ToChatRequest(message);
            chatRequest.Image = image;

            var response = await model.GenerateAsync(chatRequest);

            var document = new Document
            {
                PageContent = response,
                Metadata = new Dictionary<string, object>()
                {
                    {response, image}
                }
            };

            documents.Add(document);
        }

        var pages = await vectorCollection.AddDocumentsAsync(_embeddingModel!, documents);
    }

    [Test]
    [Explicit]
    public async Task can_query_image_against_images()
    {
        setup_image_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);
      
        var path = Path.Combine(Path.GetTempPath(), "test_image.jpg");
        var imageData = await File.ReadAllBytesAsync(path);
        var binaryData = new BinaryData(imageData, "image/jpg");

        var embeddingRequest = new EmbeddingRequest
        {
            Strings = new List<string>(),
            Images = new List<Data> { Data.FromBytes(binaryData.ToArray()) }
        };
        var embedding = await _embeddingModel!.CreateEmbeddingsAsync(embeddingRequest)
            .ConfigureAwait(false);

        var floats = embedding.ToSingleArray();
        var similaritySearchByVectorAsync = await vectorCollection.SearchAsync(floats).ConfigureAwait(false);

        Console.WriteLine("Count: " + similaritySearchByVectorAsync.Items.Count);
    }

    [Test]
    [Explicit]
    public async Task can_query_text_against_images()
    {
        setup_image_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);

        var llm = new Claude3SonnetModel(_provider!);

        var promptText =
            @"Use the following pieces of context to answer the question at the end. If the answer is not in context then just say that you don't know, don't try to make up an answer. Keep the answer as short as possible.

{context}

Question: {question}
Helpful Answer:";

        var chain =
            Set("tell me about the orange shirt", outputKey: "question")                     // set the question
            | RetrieveDocuments(vectorCollection, _embeddingModel!, inputKey: "question", outputKey: "documents", amount: 10) // take 5 most similar documents
            | StuffDocuments(inputKey: "documents", outputKey: "context")                       // combine documents together and put them into context
            | Template(promptText)                                                              // replace context and question in the prompt with their values
            | LLM(llm);                                                                       // send the result to the language model

        var res = await chain.Run("text");
        Console.WriteLine(res);
    }

    #endregion

    #region Query Simple Documents

    public void setup_document_tests()
    {
        _indexName = "test-index";
        var username = Environment.GetEnvironmentVariable("OPENSEARCH_USERNAME");
        var endpoint = Environment.GetEnvironmentVariable("OPENSEARCH_URI");
        var uri = new Uri(endpoint!);
        //var uri = new Uri("http://localhost:9200");
        var password = Environment.GetEnvironmentVariable("OPENSEARCH_INITIAL_ADMIN_PASSWORD");
        _options = new OpenSearchVectorDatabaseOptions
        {
            ConnectionUri = uri,
            Username = username,
            Password = password,
        };
        _dimensions = 1536;

        _provider = new BedrockProvider();
        _embeddingModel = new TitanEmbedTextV1Model(_provider)
        {
            Settings = new BedrockEmbeddingSettings
            {
                Dimensions = _dimensions
            }
        };
        _vectorDatabase = new OpenSearchVectorDatabase(_options);
    }

    [Test]
    [Explicit]
    public async Task index_test_documents()
    {
        setup_document_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);

        var documents = new[]
        {
            "I spent entire day watching TV",
            "My dog's name is Bob",
            "The car is orange",
            "This icecream is delicious",
            "It is cold in space",
        }.ToDocuments();

        var pages = await vectorCollection.AddDocumentsAsync(_embeddingModel!, documents);
        Console.WriteLine("pages: " + pages.Count());
    }

    [Test]
    [Explicit]
    public async Task can_query_test_documents()
    {
        setup_document_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);

        var llm = new Claude3SonnetModel(_provider!);

        const string question = "what color is the car?";

        var promptText =
            @"Use the following pieces of context to answer the question at the end. If the answer is not in context then just say that you don't know, don't try to make up an answer. Keep the answer as short as possible.

{context}

Question: {question}
Helpful Answer:";
        var chain =
            Set(question, outputKey: "question")
            | RetrieveDocuments(vectorCollection, _embeddingModel!, inputKey: "question", outputKey: "documents", amount: 2)
            | StuffDocuments(inputKey: "documents", outputKey: "context")
            | Template(promptText)
            | LLM(llm);


        var res = await chain.Run("text");
        Console.WriteLine(res);
    }

    #endregion

    #region Query Pdf Book

    [Test]
    [Explicit]
    public async Task index_harry_potter_book()
    {
        setup_document_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);

        var pdfSource = new PdfPigPdfSource("x:\\Harry-Potter-Book-1.pdf");
        var documents = await pdfSource.LoadAsync();

        var pages = await vectorCollection.AddDocumentsAsync(_embeddingModel!, documents);
        Console.WriteLine("pages: " + pages.Count());
    }

    [Test]
    [Explicit]
    public async Task can_query_harry_potter_book()
    {
        setup_document_tests();
        var vectorCollection = await _vectorDatabase!.GetOrCreateCollectionAsync(_indexName!, _dimensions);

        var llm = new Claude3SonnetModel(_provider!);

        var promptText =
            @"Use the following pieces of context to answer the question at the end. If the answer is not in context then just say that you don't know, don't try to make up an answer. Keep the answer as short as possible.

{context}

Question: {question}
Helpful Answer:";

        var chain =
            //Set("what color is the car?", outputKey: "question")                     // set the question
            //Set("Hagrid was looking for the golden key.  Where was it?", outputKey: "question")                     // set the question
            // Set("Who was on the Dursleys front step?", outputKey: "question")                     // set the question
            Set("Who was drinking a unicorn blood?", outputKey: "question")                     // set the question
            | RetrieveDocuments(vectorCollection, _embeddingModel!, inputKey: "question", outputKey: "documents", amount: 10) // take 5 most similar documents
            | StuffDocuments(inputKey: "documents", outputKey: "context")                       // combine documents together and put them into context
            | Template(promptText)                                                              // replace context and question in the prompt with their values
            | LLM(llm);                                                                       // send the result to the language model

        var res = await chain.Run("text");
        Console.WriteLine(res);
    }

    #endregion
}