using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WinDynamicIsland.Models;

namespace WinDynamicIsland.Services;

public sealed class AgentApiService
{
    private readonly HttpClient _httpClient;
    private readonly AgentOptions _options;

    public AgentApiService()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        _options = configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
        _httpClient = new HttpClient();

        foreach (var header in _options.AdditionalHeaders)
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            var value = string.IsNullOrWhiteSpace(_options.AuthHeaderValuePrefix)
                ? _options.ApiKey
                : $"{_options.AuthHeaderValuePrefix} {_options.ApiKey}";
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(_options.AuthHeaderName, value);
        }
    }

    public async Task<AgentResponse> SendVoiceAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AgentEndpointUrl))
        {
            throw new InvalidOperationException("AgentEndpointUrl is missing in appsettings.json.");
        }

        using var form = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, _options.AudioFormFieldName, "voice-message.wav");

        using var response = await _httpClient.PostAsync(_options.AgentEndpointUrl, form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new AgentResponse
            {
                AudioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
                AudioContentType = contentType
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseJsonResponse(json);
    }

    public async Task<byte[]?> DownloadAudioAsync(string? audioUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            return null;
        }

        return await _httpClient.GetByteArrayAsync(audioUrl, cancellationToken).ConfigureAwait(false);
    }

    private static AgentResponse ParseJsonResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var result = new AgentResponse
        {
            Text = ReadString(root, "text") ?? ReadString(root, "reply") ?? ReadString(root, "message"),
            AudioUrl = ReadString(root, "audioUrl") ?? ReadString(root, "audio_url") ?? ReadString(root, "audio"),
            AudioContentType = ReadString(root, "audioContentType") ?? ReadString(root, "audio_content_type")
        };

        var audioBase64 = ReadString(root, "audioBase64") ?? ReadString(root, "audio_base64");
        if (!string.IsNullOrWhiteSpace(audioBase64))
        {
            result.AudioBytes = Convert.FromBase64String(audioBase64);
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
