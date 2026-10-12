using System.IO;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnityTests.Editor.Helpers
{
    public class ProjectIdentityDisplayNameTests
    {
        private string _originalDisplayName;

        [SetUp]
        public void SetUp()
        {
            _originalDisplayName = ProjectIdentityUtility.GetCustomDisplayName();
        }

        [TearDown]
        public void TearDown()
        {
            ProjectIdentityUtility.SetCustomDisplayName(_originalDisplayName);
        }

        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase("   ", "")]
        [TestCase("  Lobby-PC1  ", "Lobby-PC1")]
        [TestCase("Game@PC1", "Game-PC1")]
        public void NormalizeDisplayName_TrimsAndRemovesInstanceSeparator(string input, string expected)
        {
            Assert.AreEqual(expected, ProjectIdentityUtility.NormalizeDisplayName(input));
        }

        [Test]
        public void GetProjectName_ReturnsCustomDisplayName_WhenSet()
        {
            ProjectIdentityUtility.SetCustomDisplayName("Lobby-PC1");

            Assert.AreEqual("Lobby-PC1", ProjectIdentityUtility.GetProjectName());
        }

        [Test]
        public void GetProjectName_FallsBackToFolderName_WhenCleared()
        {
            ProjectIdentityUtility.SetCustomDisplayName("Lobby-PC1");
            ProjectIdentityUtility.SetCustomDisplayName(string.Empty);

            Assert.AreEqual(string.Empty, ProjectIdentityUtility.GetCustomDisplayName());
            Assert.AreEqual(
                Path.GetFileName(Path.GetDirectoryName(Application.dataPath)),
                ProjectIdentityUtility.GetProjectName());
        }
    }
}
