using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Octokit.Webhooks.Models;
using OoLunar.GitcordSymlink.Configuration;

namespace OoLunar.GitcordSymlink.GitHub
{
    public sealed class GitHubApiRoutes
    {
        private static readonly FrozenDictionary<AppEvent, string> _appEventEnumMembers = Enum.GetNames<AppEvent>().ToFrozenDictionary(Enum.Parse<AppEvent>, name => typeof(AppEvent).GetField(name, BindingFlags.Static | BindingFlags.Public)!.GetCustomAttribute<EnumMemberAttribute>()!.Value!);

        private readonly GitHubConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public GitHubApiRoutes(GitcordSymlinkConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration.GitHub;
            _httpClient = httpClient;
        }

        public async ValueTask<ApiResult<GitHubRepository>> GetRepositoryAsync(string accessToken, string repositoryFullName)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{repositoryFullName}");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            return new()
            {
                StatusCode = response.StatusCode,
                Error = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                Value = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<GitHubRepository>() : null
            };
        }

        public async ValueTask<ApiResult<bool>> UpdateRepositoryArchiveStatusAsync(string accessToken, string repositoryFullName, bool isArchived)
        {
            using HttpRequestMessage request = new(HttpMethod.Patch, $"https://api.github.com/repos/{repositoryFullName}");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Content = JsonContent.Create(new
            {
                archived = isArchived
            });

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            return new()
            {
                StatusCode = response.StatusCode,
                Error = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                Value = response.StatusCode == HttpStatusCode.OK
            };
        }

        public async ValueTask<ApiResult<IReadOnlyList<ulong>>> ListRepositoryDiscordWebhooksAsync(string accessToken, string repositoryFullName)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{repositoryFullName}/hooks");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            return new()
            {
                StatusCode = response.StatusCode,
                Error = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                Value = response.IsSuccessStatusCode ? GrabIds(await response.Content.ReadAsByteArrayAsync()) : null
            };

            static IReadOnlyList<ulong> GrabIds(byte[] responseContent)
            {
                List<ulong> ids = [];
                Utf8JsonReader reader = new(responseContent);
                while (reader.Read())
                {
                    // Search for id property
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "id")
                    {
                        continue;
                    }

                    // Store the id value
                    reader.Read();
                    ulong id = reader.GetUInt64();
                    while (reader.Read())
                    {
                        // Search for config property
                        if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "config")
                        {
                            continue;
                        }

                        while (reader.Read())
                        {
                            // Search for url property
                            if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "url")
                            {
                                continue;
                            }

                            reader.Read();
                            string? url = reader.GetString();
                            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("https://discord.com/api/webhooks", StringComparison.Ordinal))
                            {
                                ids.Add(id);
                            }

                            break;
                        }

                        break;
                    }
                }

                return ids;
            }
        }

        public async ValueTask<ApiResult<bool>> DeleteWebhookAsync(string accessToken, string repositoryFullName, ulong hookId)
        {
            using HttpRequestMessage deleteWebhookRequest = new(HttpMethod.Delete, $"https://api.github.com/repos/{repositoryFullName}/hooks/{hookId}");
            deleteWebhookRequest.Headers.Add("Accept", "application/vnd.github+json");
            deleteWebhookRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            deleteWebhookRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage deleteWebhookResponse = await _httpClient.SendAsync(deleteWebhookRequest);
            return new()
            {
                StatusCode = deleteWebhookResponse.StatusCode,
                Error = !deleteWebhookResponse.IsSuccessStatusCode ? await deleteWebhookResponse.Content.ReadAsStringAsync() : null,
                Value = deleteWebhookResponse.StatusCode == HttpStatusCode.NoContent
            };
        }

        public async ValueTask<ApiResult<ulong>> InstallWebhookAsync(string accessToken, string repositoryFullName, string webhook, IReadOnlyList<AppEvent> events)
        {
            using HttpRequestMessage createWebhookRequest = new(HttpMethod.Post, $"https://api.github.com/repos/{repositoryFullName}/hooks");
            createWebhookRequest.Headers.Add("Accept", "application/vnd.github+json");
            createWebhookRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            createWebhookRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            createWebhookRequest.Content = JsonContent.Create(new
            {
                name = "web",
                active = true,
                events = events.Select(e => _appEventEnumMembers[e]).ToArray(),
                config = new
                {
                    url = webhook,
                    content_type = "json"
                }
            });

            using HttpResponseMessage createWebhookResponse = await _httpClient.SendAsync(createWebhookRequest);
            return new()
            {
                StatusCode = createWebhookResponse.StatusCode,
                Error = !createWebhookResponse.IsSuccessStatusCode ? await createWebhookResponse.Content.ReadAsStringAsync() : null,
                Value = createWebhookResponse.IsSuccessStatusCode ? GrabWebhookIdAsync(await createWebhookResponse.Content.ReadAsByteArrayAsync()) : 0
            };

            static ulong GrabWebhookIdAsync(byte[] content)
            {
                Utf8JsonReader reader = new(content);
                while (reader.Read())
                {
                    // Search for id property
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "id" || !reader.Read())
                    {
                        continue;
                    }

                    return reader.GetUInt64();
                }

                throw new InvalidOperationException($"Failed to find the webhook id: {Encoding.UTF8.GetString(content)}");
            }
        }

        public async ValueTask<ApiResult<object>> TestWebhookAsync(string accessToken, string repositoryFullName, ulong hookId)
        {
            using HttpRequestMessage testWebhookRequest = new(HttpMethod.Post, $"https://api.github.com/repos/{repositoryFullName}/hooks/{hookId}/tests");
            testWebhookRequest.Headers.Add("Accept", "application/vnd.github+json");
            testWebhookRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            testWebhookRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage testWebhookResponse = await _httpClient.SendAsync(testWebhookRequest);
            return new()
            {
                StatusCode = testWebhookResponse.StatusCode,
                Error = !testWebhookResponse.IsSuccessStatusCode ? await testWebhookResponse.Content.ReadAsStringAsync() : null,
                Value = null
            };
        }

        [SuppressMessage("Roslyn", "IDE0046", Justification = "Terinary rabbit hole.")]
        public async ValueTask<(string, DateTimeOffset)> GetAccessTokenAsync(long installationId, IEnumerable<long> repositoryIds)
        {
            // Generate a JWT
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string jwt = GenerateJWT(now);

            using HttpRequestMessage request = new(HttpMethod.Post, $"https://api.github.com/app/installations/{installationId}/access_tokens");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {jwt}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Content = JsonContent.Create(new
            {
                repository_ids = repositoryIds,
                permissions = new
                {
                    administration = "write",
                    repository_hooks = "write",
                    organization_hooks = "write"
                }
            });

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to get access token: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("token", out JsonElement tokenJson))
            {
                throw new InvalidOperationException($"Failed to get access token, missing 'token' element: {json}");
            }
            else if (tokenJson.GetString() is not string token || string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException($"Failed to get access token, 'token' element is null or whitespace: {json}");
            }
            else if (!json.TryGetProperty("expires_at", out JsonElement expiresAtJson))
            {
                throw new InvalidOperationException($"Failed to get access token, missing 'expires_at' element: {json}");
            }
            else if (!expiresAtJson.TryGetDateTimeOffset(out DateTimeOffset expiresAt))
            {
                throw new InvalidOperationException($"Failed to get access token, 'expires_at' element is not a valid date: {json}");
            }
            else
            {
                return (token, expiresAt);
            }
        }

        public string GenerateJWT(DateTimeOffset now)
        {
            string header = ConvertToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "RS256",
                typ = "JWT"
            }));

            long nowUnixTimestamp = now.ToUnixTimeSeconds();
            string payload = ConvertToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = nowUnixTimestamp - 10,
                exp = nowUnixTimestamp + 600,
                iss = _configuration.ClientId
            }));

            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(_configuration.PrivateKeyPath));

            string unsignedJwt = $"{header}.{payload}";
            byte[] signatureBytes = rsa.SignData(Encoding.UTF8.GetBytes(unsignedJwt), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string signature = ConvertToBase64Url(signatureBytes);

            return $"{unsignedJwt}.{signature}";
        }

        private static string ConvertToBase64Url(byte[] input) => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
