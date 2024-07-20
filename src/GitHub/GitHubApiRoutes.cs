using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Octokit.Webhooks.Models;
using Octokit.Webhooks.Models.PingEvent;
using OoLunar.GitcordSymlink.Configuration;

namespace OoLunar.GitcordSymlink.GitHub
{
    public sealed class GitHubApiRoutes
    {
        private readonly GitHubConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public GitHubApiRoutes(GitcordSymlinkConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration.GitHub;
            _httpClient = httpClient;
        }

        public async ValueTask<ApiResult<Repository>> GetRepositoryAsync(string accessToken, string repositoryFullName)
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
                Value = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<Repository>() : null
            };
        }

        public async ValueTask<ApiResult<Hook>> InstallWebhookAsync(string accessToken, string repositoryFullName, string webhook, AppEvent events)
        {
            using HttpRequestMessage createWebhookRequest = new(HttpMethod.Post, $"https://api.github.com/repos/{repositoryFullName}/hooks");
            createWebhookRequest.Headers.Add("Accept", "application/vnd.github+json");
            createWebhookRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            createWebhookRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            createWebhookRequest.Content = JsonContent.Create(new
            {
                name = "web",
                active = true,
                events = events.ToString().Split(','),
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
                Value = createWebhookResponse.IsSuccessStatusCode ? await createWebhookResponse.Content.ReadFromJsonAsync<Hook>() : null
            };
        }

        public async ValueTask<ApiResult<object>> TestWebhookAsync(string accessToken, string repositoryFullName, long hookId)
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
