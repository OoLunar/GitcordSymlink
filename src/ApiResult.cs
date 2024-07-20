using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace OoLunar.GitcordSymlink
{
    public readonly struct ApiResult<T>
    {
        public required HttpStatusCode StatusCode { get; init; }
        public required string? Error { get; init; }
        public required T? Value { get; init; }

        [MemberNotNullWhen(true, nameof(Value))]
        [MemberNotNullWhen(false, nameof(Error))]
        public bool IsSuccessful => Error is null;
    }
}
