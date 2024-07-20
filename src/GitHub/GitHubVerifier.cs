using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using OoLunar.GitcordSymlink.Configuration;

namespace OoLunar.GitcordSymlink.GitHub
{
    public sealed class GitHubVerifier : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [];
        private readonly GitHubConfiguration _configuration;
        private readonly byte[] _webhookSecretBytes;

        public GitHubVerifier(GitcordSymlinkConfiguration configuration)
        {
            _configuration = configuration.GitHub;
            _webhookSecretBytes = Encoding.UTF8.GetBytes(_configuration.WebhookSecret);
        }

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Route.AbsolutePath.StartsWith("/github", StringComparison.OrdinalIgnoreCase))
            {
                // Skip this responder if the route doesn't start with /github
                return Result.Failure<HyperStatus>();
            }
            else if (!context.Headers.TryGetValue("Content-Length", out string? contentLengthString))
            {
                return HyperStatus.BadRequest(new Error("Missing content length."));
            }
            else if (!int.TryParse(contentLengthString, out int contentLength))
            {
                return HyperStatus.BadRequest(new Error("Invalid content length."));
            }
            else
            {
                // Read the whole payload into memory because we must verify the signature
                int bytesRead = 0;
                byte[] bodyBuffer = ArrayPool<byte>.Shared.Rent(contentLength);
                ReadResult readResult;
                do
                {
                    readResult = await context.BodyReader.ReadAsync(cancellationToken);
                    if (readResult.Buffer.Length > contentLength || (bytesRead + readResult.Buffer.Length) > contentLength)
                    {
                        return HyperStatus.BadRequest(new Error("Content length exceeded."));
                    }

                    bytesRead += ParseBody(context, readResult, bodyBuffer.AsSpan(bytesRead, (int)readResult.Buffer.Length));
                    if (readResult.IsCompleted)
                    {
                        if (bytesRead != contentLength)
                        {
                            return HyperStatus.BadRequest(new Error("Content length mismatch."));
                        }

                        break;
                    }
                } while (bytesRead != contentLength);

                // Store the body
                context.Metadata["body"] = Encoding.UTF8.GetString(bodyBuffer, 0, bytesRead);

                // If there's no signature then skip verification
                if (!context.Headers.TryGetValue("X-Hub-Signature-256", out string? signature))
                {
                    return Result.Success<HyperStatus>();
                }
                // Verify the signature
                else if (!TryVerifySignature(bodyBuffer.AsSpan(0, bytesRead), _webhookSecretBytes, signature))
                {
                    return HyperStatus.Unauthorized(new Error("Invalid signature."));
                }

                // Return success
                return Result.Success<HyperStatus>();
            }
        }

        public static int ParseBody(HyperContext context, ReadResult readResult, Span<byte> bodyBuffer)
        {
            if (!context.Metadata.ContainsKey("account"))
            {
                Utf8JsonReader utf8JsonReader = new(readResult.Buffer);
                while (utf8JsonReader.TokenType != JsonTokenType.PropertyName || utf8JsonReader.GetString() != "full_name")
                {
                    // Keep reading until we find the full name property
                    if (!utf8JsonReader.Read())
                    {
                        break;
                    }
                }

                // We're currently on the property name, so move to the value
                utf8JsonReader.Read();

                // Split the full name into account and repository
                string[] fullName = utf8JsonReader.GetString()!.Split('/');
                context.Metadata["account"] = fullName[0];
                context.Metadata["repository"] = fullName[1];
            }

            readResult.Buffer.CopyTo(bodyBuffer);
            context.BodyReader.AdvanceTo(readResult.Buffer.End);
            return (int)readResult.Buffer.Length;
        }

        public static bool TryVerifySignature(ReadOnlySpan<byte> body, ReadOnlySpan<byte> secretKey, string signature)
            => $"sha256={Convert.ToHexString(HMACSHA256.HashData(secretKey, body))}".Equals(signature, StringComparison.OrdinalIgnoreCase);
    }
}
