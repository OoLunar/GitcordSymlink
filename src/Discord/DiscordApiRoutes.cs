using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OoLunar.GitcordSymlink.Configuration;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;

namespace OoLunar.GitcordSymlink.Discord
{
    public sealed class DiscordApiRoutes
    {
        private readonly DiscordConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public DiscordApiRoutes(GitcordSymlinkConfiguration configuration, HttpClient httpClient, IOptionsSnapshot<JsonSerializerOptions> jsonSerializerOptions)
        {
            _configuration = configuration.Discord;
            _httpClient = httpClient;
            _jsonSerializerOptions = jsonSerializerOptions.Get("HyperSharp");
        }

        public async ValueTask<ApiResult<IReadOnlyList<IApplicationCommand>>> RegisterApplicationCommandsAsync(IReadOnlyList<IBulkApplicationCommandData> applicationCommandData)
        {
            HttpRequestMessage request = new(HttpMethod.Put, $"https://discord.com/api/v10/applications/{_configuration.ApplicationId}/commands");
            request.Headers.Add("Authorization", $"Bot {_configuration.Token}");
            request.Content = JsonContent.Create(applicationCommandData, options: _jsonSerializerOptions);

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            return new ApiResult<IReadOnlyList<IApplicationCommand>>()
            {
                StatusCode = response.StatusCode,
                Error = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(),
                Value = !response.IsSuccessStatusCode ? null : await response.Content.ReadFromJsonAsync<IReadOnlyList<IApplicationCommand>>(_jsonSerializerOptions)
            };
        }

        public async ValueTask<ApiResult<Channel>> GetChannelAsync(ulong channelId)
        {
            HttpRequestMessage request = new(HttpMethod.Get, $"https://discord.com/api/v10/channels/{channelId}");
            request.Headers.Add("Authorization", $"Bot {_configuration.Token}");

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            return new ApiResult<Channel>()
            {
                StatusCode = response.StatusCode,
                Error = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(),
                Value = !response.IsSuccessStatusCode ? null : await response.Content.ReadFromJsonAsync<Channel>(_jsonSerializerOptions)
            };
        }

        public async ValueTask<ApiResult<Channel>> CreateThreadChannelAsync(ulong channelId, string fullName)
        {
            HttpRequestMessage request = new(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/threads");
            request.Headers.Add("Authorization", $"Bot {_configuration.Token}");
            request.Headers.Add("X-Audit-Log-Reason", $"Creating project thread for GitHub project '{fullName}'.");
            request.Content = JsonContent.Create(
                inputValue: new
                {
                    name = fullName.Split('/')[1],
                    message = new
                    {
                        embeds = new List<Embed>()
                        {
                            new() {
                                Colour = ColorTranslator.FromHtml("#6b73db"),
                                Description = $"Linking [`{fullName}`](https://github.com/{fullName}) from GitHub to Discord..."
                            }
                        }
                    }
                },
                options: _jsonSerializerOptions
            );

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            return new ApiResult<Channel>()
            {
                StatusCode = response.StatusCode,
                Error = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(),
                Value = !response.IsSuccessStatusCode ? null : await response.Content.ReadFromJsonAsync<Channel>(_jsonSerializerOptions)
            };
        }

        public async ValueTask<ApiResult<Webhook>> CreateWebhookAsync(IPartialChannel channel, string auditLogReason)
        {
            HttpRequestMessage requestMessage = new(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channel.ID}/webhooks");
            requestMessage.Headers.Add("Authorization", $"Bot {_configuration.Token}");
            requestMessage.Headers.Add("X-Audit-Log-Reason", auditLogReason);
            requestMessage.Content = JsonContent.Create(
                inputValue: new
                {
                    name = "Symlink Gitcord"
                },
                options: _jsonSerializerOptions
            );

            HttpResponseMessage response = await _httpClient.SendAsync(requestMessage);
            return new ApiResult<Webhook>()
            {
                StatusCode = response.StatusCode,
                Error = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(),
                Value = !response.IsSuccessStatusCode ? null : await response.Content.ReadFromJsonAsync<Webhook>(_jsonSerializerOptions)
            };
        }
    }
}
