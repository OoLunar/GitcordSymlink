using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;

namespace OoLunar.GitHubForumWebhookWorker
{
    public sealed class GitHubVerifier : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [];

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            // There should only be 3 paths in the url:
            // - The secret hex key
            // - The account name
            // - The repository name
            if (context.Route.Segments.Length == 3)
            {
                return HyperStatus.NotFound(new Error("Not found."));
            }
            else if (!context.Headers.TryGetValue("Content-Length", out string? contentLengthString))
            {
                return HyperStatus.BadRequest(new Error("Missing content length."));
            }
            else if (!int.TryParse(contentLengthString, out int contentLength))
            {
                return HyperStatus.BadRequest(new Error("Invalid content length."));
            }
            else if (!context.Headers.TryGetValue("X-Hub-Signature-256", out string? signature))
            {
                return HyperStatus.BadRequest(new Error("Missing signature."));
            }
            else
            {
                // Read the whole payload into memory because we must verify the signature
                int bytesRead = 0;
                byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
                ReadResult readResult;
                do
                {
                    readResult = await context.BodyReader.ReadAsync(cancellationToken);
                    if (readResult.Buffer.Length > contentLength || (bytesRead + readResult.Buffer.Length) > contentLength)
                    {
                        return HyperStatus.BadRequest(new Error("Content length exceeded."));
                    }

                    readResult.Buffer.CopyTo(body.AsSpan(bytesRead, (int)readResult.Buffer.Length));
                    bytesRead += (int)readResult.Buffer.Length;
                    context.BodyReader.AdvanceTo(readResult.Buffer.End);

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
                context.Metadata["body"] = Encoding.UTF8.GetString(body, 0, bytesRead);

                // Hex to bytes
                byte[] secretKey = Encoding.UTF8.GetBytes(context.Route.AbsolutePath.Split('/')[1]);

                // Verify the signature
                return VerifySignature(body.AsSpan(0, bytesRead), secretKey, signature);
            }
        }

        public static Result<HyperStatus> VerifySignature(ReadOnlySpan<byte> body, ReadOnlySpan<byte> secretKey, string signature)
            => $"sha256={Convert.ToHexString(HMACSHA256.HashData(secretKey, body))}".Equals(signature, StringComparison.OrdinalIgnoreCase)
                ? Result.Success<HyperStatus>()
                : HyperStatus.Unauthorized(new Error("Invalid signature"));
    }
}
