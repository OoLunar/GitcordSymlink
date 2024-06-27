using HyperSharp.Protocol;
using HyperSharp.Results;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OoLunar.GitHubForumWebhookWorker.Tests
{
    [TestClass]
    public class GitHubVerifierTests
    {
        [TestMethod]
        public void VerifySignature()
        {
            Result<HyperStatus> result = GitHubVerifier.VerifySignature(
                body: "Hello, World!"u8,
                secretKey: "It's a Secret to Everybody"u8,
                signature: "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17"
            );

            Assert.IsTrue(result.IsSuccess && !result.HasValue);
        }
    }
}
