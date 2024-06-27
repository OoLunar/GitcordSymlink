using Microsoft.VisualStudio.TestTools.UnitTesting;
using OoLunar.GitHubForumWebhookWorker.GitHub;

namespace OoLunar.GitHubForumWebhookWorker.Tests
{
    [TestClass]
    public class GitHubVerifierTests
    {
        [TestMethod]
        public void VerifySignature() => Assert.IsTrue(GitHubVerifier.TryVerifySignature(
            body: "Hello, World!"u8,
            secretKey: "It's a Secret to Everybody"u8,
            signature: "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17"
        ));
    }
}
