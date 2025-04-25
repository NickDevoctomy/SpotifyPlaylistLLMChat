using OpenAI;
using OpenAI.Chat;
using SpotifyAPI.Web;
using System.Text;
using System.Text.Json;

var openAiClient = new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)!);
var chatClient = openAiClient.GetChatClient("gpt-4o");

var spotifyAccessToken = await GetSpotifyAccessTokenAsync(
    Environment.GetEnvironmentVariable("SPOTIFY_API_CLIENTID", EnvironmentVariableTarget.User)!,
    Environment.GetEnvironmentVariable("SPOTIFY_API_SECRET", EnvironmentVariableTarget.User)!);

var api = new SpotifyClient(spotifyAccessToken!, "Bearer");
//var me = await api.UserProfile.Current(); we are authenticated as the app, so don't have access to user profile, we must be provided the user id beforehand

async Task<string?> GetSpotifyAccessTokenAsync(string clientId, string clientSecret)
{
    using var client = new HttpClient();

    var content = new StringContent($"grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}", Encoding.UTF8, "application/x-www-form-urlencoded");

    var response = await client.PostAsync("https://accounts.spotify.com/api/token", content);

    if (!response.IsSuccessStatusCode)
        return null;

    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("access_token").GetString();
}
async Task<List<FullPlaylist>?> GetAllMyPlaylists(CancellationToken cancellationToken)
{
    var allPlayLists = new List<FullPlaylist>();
    var playlists = await api.Playlists.GetUsers("firkinfedup");
    if (playlists == null ||
        playlists.Items == null ||
        playlists.Items.Count == 0)
    {
        return null;
    }
    allPlayLists.AddRange(playlists.Items);
    while (allPlayLists.Count < playlists.Total)
    {
        playlists = await api.NextPage(playlists);
        if (playlists == null ||
            playlists.Items == null ||
            playlists.Items.Count == 0)
        {
            break;
        }

        allPlayLists.AddRange(playlists.Items);
    }

    return allPlayLists;
}

async Task<FullPlaylist> GetPlaylist(string playlistId, CancellationToken cancellationToken)
{
    return await api.Playlists.Get(playlistId, cancellationToken);
}

ChatTool getAllMySpotifyPlaylistsTool = ChatTool.CreateFunctionTool(
    functionName: nameof(GetAllMyPlaylists),
    functionDescription: "Get all my Spotify playlists. This will return all playlists belonging to the current user, but will not get all of the tracks for each playlist."
);

ChatTool getPlaylistTool = ChatTool.CreateFunctionTool(
    functionName: nameof(GetPlaylist),
    functionDescription: "Get a Spotify playlist and all of its tracks.",
        functionParameters: BinaryData.FromBytes(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "object",
                properties = new
                {
                    id = new
                    {
                        type = "string",
                        description = "Unique Id of the playlist to get."
                    }
                },
                required = new[] { "id" }
            }))
);

var question = "I would like to know if any tracks feature more than once across my 'siren sessions' playlists, excluding the 'best of ones'. I don't think there are any duplicates, but see if you can find any, then tell me the tracks and playlists they feature in.";
List<ChatMessage> messages =
[
    new UserChatMessage(question),
];

ChatCompletionOptions options = new()
{
    Tools = { getAllMySpotifyPlaylistsTool, getPlaylistTool },
    Temperature = 0.1f // had to lower this as with higher values it just makes shit up
};

bool requiresAction;

do
{
    requiresAction = false;
    ChatCompletion completion = chatClient.CompleteChat(messages, options);

    switch (completion.FinishReason)
    {
        case ChatFinishReason.Stop:
            {
                messages.Add(new AssistantChatMessage(completion));
                break;
            }

        case ChatFinishReason.ToolCalls:
            {
                messages.Add(new AssistantChatMessage(completion));

                foreach (ChatToolCall toolCall in completion.ToolCalls)
                {
                    switch (toolCall.FunctionName)
                    {
                        case nameof(GetAllMyPlaylists):
                            {
                                Console.WriteLine("Calling GetAllMyPlaylists...");
                                var allMyPlaylists = await GetAllMyPlaylists(CancellationToken.None);
                                var truncated = allMyPlaylists.Select(x => new { x.Id, x.Name });
                                var playlistsText = string.Join("\n", truncated.Select(x => $"{x.Name} ({x.Id})"));
                                messages.Add(new ToolChatMessage(toolCall.Id, playlistsText));
                                break;
                            }

                        case nameof(GetPlaylist):
                            {
                                Console.WriteLine("Calling GetPlaylist...");
                                using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                bool hasId = argumentsJson.RootElement.TryGetProperty("id", out JsonElement id);

                                if (!hasId)
                                {
                                    throw new ArgumentNullException(nameof(id), "The id argument is required.");
                                }

                                var playlist = await GetPlaylist(id.GetString()!, CancellationToken.None);
                                var tracks = playlist.Tracks.Items.Select(x => $"{((FullTrack)x.Track).Artists[0].Name} - {((FullTrack)x.Track).Name}");
                                messages.Add(new ToolChatMessage(toolCall.Id, string.Join('\n', tracks)));
                                break;
                            }

                        default:
                            {
                                // Handle other unexpected calls.
                                throw new NotImplementedException();
                            }
                    }
                }

                requiresAction = true;
                break;
            }

        case ChatFinishReason.Length:
            throw new NotImplementedException("Incomplete model output due to MaxTokens parameter or token limit exceeded.");

        case ChatFinishReason.ContentFilter:
            throw new NotImplementedException("Omitted content due to a content filter flag.");

        case ChatFinishReason.FunctionCall:
            throw new NotImplementedException("Deprecated in favor of tool calls.");

        default:
            throw new NotImplementedException(completion.FinishReason.ToString());
    }
} while (requiresAction);

foreach (var message in messages)
{
    foreach(var part in message.Content)
    {
        Console.WriteLine(part.Text);
    }
}