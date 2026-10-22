using System;
using System.IO;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor.Helpers
{
    public class ProjectIdentityTests
    {
        private string _originalHostLabel;

        [SetUp]
        public void SetUp()
        {
            _originalHostLabel = ProjectIdentityUtility.GetCustomHostLabel();
        }

        [TearDown]
        public void TearDown()
        {
            ProjectIdentityUtility.SetHostLabel(_originalHostLabel);
        }

        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase("   ", "")]
        [TestCase("  Lab PC 2  ", "Lab PC 2")]
        [TestCase("Lab@PC2", "Lab@PC2")]
        public void NormalizeHostLabel_TrimsAndTreatsBlankAsUnset(string input, string expected)
        {
            Assert.AreEqual(expected, ProjectIdentityUtility.NormalizeHostLabel(input));
        }

        [Test]
        public void GetHostLabel_ReturnsCustomLabel_WhenSet()
        {
            ProjectIdentityUtility.SetHostLabel("Lab PC 2");

            Assert.AreEqual("Lab PC 2", ProjectIdentityUtility.GetHostLabel());
        }

        [Test]
        public void GetHostLabel_FallsBackToComputerName_WhenCleared()
        {
            ProjectIdentityUtility.SetHostLabel("Lab PC 2");
            ProjectIdentityUtility.SetHostLabel(string.Empty);

            Assert.AreEqual(string.Empty, ProjectIdentityUtility.GetCustomHostLabel());
            Assert.AreEqual(Environment.MachineName, ProjectIdentityUtility.GetHostLabel());
        }

        [Test]
        public void GetProjectName_IsFolderName_EvenWhenHostLabelIsSet()
        {
            ProjectIdentityUtility.SetHostLabel("Lab PC 2");

            Assert.AreEqual(
                Path.GetFileName(Path.GetDirectoryName(Application.dataPath)),
                ProjectIdentityUtility.GetProjectName());
        }

        [Test]
        public void GetProjectGuid_IsProductGuidAsHex()
        {
            string expected = PlayerSettings.productGUID.ToString("N");

            Assert.AreEqual(32, expected.Length);
            Assert.AreEqual(expected, ProjectIdentityUtility.GetProjectGuid());
        }

        [Test]
        public void GetInstanceRoutingKey_OutsideRemote_IsProjectHash()
        {
            Assert.AreEqual(
                ProjectIdentityUtility.GetProjectHash(),
                ProjectIdentityUtility.GetInstanceRoutingKey(remoteScope: false));
        }

        [Test]
        public void GetInstanceRoutingKey_InRemote_DiffersFromProjectHash()
        {
            string remoteKey = ProjectIdentityUtility.GetInstanceRoutingKey(remoteScope: true);

            Assert.AreNotEqual(ProjectIdentityUtility.GetProjectHash(), remoteKey);
            StringAssert.IsMatch("^[0-9a-f]{16}$", remoteKey);
        }

        [Test]
        public void GetInstanceRoutingKey_InRemote_IgnoresHostLabel()
        {
            ProjectIdentityUtility.SetHostLabel("Lab PC 2");
            string before = ProjectIdentityUtility.GetInstanceRoutingKey(remoteScope: true);
            ProjectIdentityUtility.SetHostLabel("Lab PC 3");

            Assert.AreEqual(before, ProjectIdentityUtility.GetInstanceRoutingKey(remoteScope: true));
        }

        [Test]
        public void ComputeRemoteRoutingKey_SamePathOnTwoMachines_Differs()
        {
            const string dataPath = "C:/Users/dev/Desktop/Projects/Game/Assets";

            Assert.AreNotEqual(
                ProjectIdentityUtility.ComputeRemoteRoutingKey("device-a|PC-A", dataPath),
                ProjectIdentityUtility.ComputeRemoteRoutingKey("device-b|PC-B", dataPath));
        }

        [Test]
        public void ComputeRemoteRoutingKey_SameMachineAndPath_IsStable()
        {
            const string dataPath = "C:/Users/dev/Desktop/Projects/Game/Assets";

            Assert.AreEqual(
                ProjectIdentityUtility.ComputeRemoteRoutingKey("device-a|PC-A", dataPath),
                ProjectIdentityUtility.ComputeRemoteRoutingKey("device-a|PC-A", dataPath));
        }

        [Test]
        public void ComputeRemoteRoutingKey_TwoCheckoutsOnOneMachine_Differ()
        {
            Assert.AreNotEqual(
                ProjectIdentityUtility.ComputeRemoteRoutingKey("device-a|PC-A", "C:/work/Game/Assets"),
                ProjectIdentityUtility.ComputeRemoteRoutingKey("device-a|PC-A", "C:/work/Game-2/Assets"));
        }
    }
}
