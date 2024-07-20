using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using OoLunar.GitcordSymlink.Configuration;

namespace OoLunar.GitcordSymlink.Discord
{
    public sealed class DiscordVerifier : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [];
        private readonly byte[] _publicKey;

        public DiscordVerifier(GitcordSymlinkConfiguration configuration) => _publicKey = Convert.FromHexString(configuration.Discord.PublicKey);

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Route.AbsolutePath.StartsWith("/discord", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<HyperStatus>();
            }

            if (!context.Headers.TryGetValue("Content-Length", out string? contentLengthString) || !int.TryParse(contentLengthString, out int contentLength))
            {
                return HyperStatus.BadRequest(new Error("Invalid content length."));
            }

            if (!context.Headers.TryGetValue("X-Signature-Ed25519", out string? signature) || !context.Headers.TryGetValue("X-Signature-Timestamp", out string? timestamp))
            {
                return HyperStatus.BadRequest(new Error("Missing signature headers."));
            }

            int timestampByteCount = Encoding.UTF8.GetByteCount(timestamp);
            byte[] bodyBuffer = ArrayPool<byte>.Shared.Rent(contentLength + timestampByteCount);
            Encoding.UTF8.GetBytes(timestamp, bodyBuffer);

            contentLength += timestampByteCount;
            int bytesRead = timestampByteCount;
            ReadResult readResult;
            do
            {
                readResult = await context.BodyReader.ReadAsync(cancellationToken);
                if (readResult.Buffer.Length > contentLength || (bytesRead + readResult.Buffer.Length) > contentLength)
                {
                    return HyperStatus.BadRequest(new Error("Content length exceeded."));
                }

                readResult.Buffer.CopyTo(bodyBuffer.AsSpan(bytesRead, (int)readResult.Buffer.Length));
                context.BodyReader.AdvanceTo(readResult.Buffer.End);
                bytesRead += (int)readResult.Buffer.Length;
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
            context.Metadata["body"] = Encoding.UTF8.GetString(bodyBuffer, timestampByteCount, bytesRead - timestampByteCount);

            // Verify the signature
            return Ed25519.TryVerifySignature(bodyBuffer.AsSpan(0, bytesRead), _publicKey, Convert.FromHexString(signature))
                ? Result.Success<HyperStatus>()
                : HyperStatus.Unauthorized(new Error("Invalid signature."));
        }
    }
}
