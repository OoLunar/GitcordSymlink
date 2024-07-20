// This file was originally pulled from https://github.com/VelvetToroyashi/RemoraHTTPInteractions/blob/4a94b104535f150053f06b1aa99c86d9b84d81ef/RemoraHTTPInteractions/InteractionResponseData.cs#L8
// All credit goes to the original author, VelvetToroyashi.
// Changes made:
// - Formatting
// - Change of namespace
// - Removal of InteractionsWebhookResponseData, replaced with IInteractionResponse

using System.Threading.Tasks;
using Remora.Discord.API.Abstractions.Objects;

namespace OoLunar.GitcordSymlink.Discord.RemoraInteractions
{
    public record InteractionWebhookResponse(TaskCompletionSource<IInteractionResponse> ResponseTCS);
}
