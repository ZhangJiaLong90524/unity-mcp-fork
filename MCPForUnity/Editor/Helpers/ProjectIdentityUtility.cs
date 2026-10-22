using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Constants;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Provides shared utilities for deriving deterministic project identity information
    /// used by transport clients (hash, name, persistent session id).
    /// </summary>
    [InitializeOnLoad]
    internal static class ProjectIdentityUtility
    {
        private const string SessionPrefKey = EditorPrefKeys.SessionId;
        private static bool _legacyKeyCleared;
        private static string _cachedProjectName = "Unknown";
        private static string _cachedProjectHash = "default";
        private static string _cachedDataPath = string.Empty;
        private static string _cachedProjectGuid = string.Empty;
        private static string _cachedMachineIdentity = string.Empty;
        private static string _fallbackSessionId;
        private static bool _cacheScheduled;

        static ProjectIdentityUtility()
        {
            ScheduleCacheRefresh();
            EditorApplication.projectChanged += ScheduleCacheRefresh;
        }

        private static void ScheduleCacheRefresh()
        {
            if (_cacheScheduled)
            {
                return;
            }

            _cacheScheduled = true;
            EditorApplication.delayCall += CacheIdentityOnMainThread;
        }

        private static void CacheIdentityOnMainThread()
        {
            EditorApplication.delayCall -= CacheIdentityOnMainThread;
            _cacheScheduled = false;
            UpdateIdentityCache();
        }

        private static void UpdateIdentityCache()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath))
                {
                    return;
                }

                _cachedProjectHash = ComputeProjectHash(dataPath);
                _cachedProjectName = ComputeProjectName(dataPath);
                _cachedDataPath = dataPath;
                _cachedProjectGuid = ReadProjectGuid();
                _cachedMachineIdentity = ReadMachineIdentity();
            }
            catch
            {
                // Ignore and keep defaults
            }
        }

        /// <summary>
        /// Returns the SHA1 hash of the current project path (truncated to 16 characters).
        /// Keys this project's local state (session id, stdio bridge). The server routes by
        /// <see cref="GetInstanceRoutingKey"/>, which equals this hash outside HTTP Remote.
        /// </summary>
        public static string GetProjectHash()
        {
            EnsureIdentityCache();
            return _cachedProjectHash;
        }

        /// <summary>
        /// Returns the key the server routes this Editor by: the part after '@' in Name@hash.
        /// In HTTP Remote, Editors on several machines share one server, and a project cloned
        /// at the same path on two machines has the same project hash; the server would treat
        /// the second connection as a reconnect of the first and evict it. Mixing in the machine
        /// identity keeps their keys distinct while staying stable across reconnects. Other
        /// scopes stay on one machine, so they keep the project hash and their ids do not change.
        /// </summary>
        public static string GetInstanceRoutingKey(bool remoteScope)
        {
            string projectHash = GetProjectHash();
            if (!remoteScope)
            {
                return projectHash;
            }

            if (string.IsNullOrEmpty(_cachedMachineIdentity) || string.IsNullOrEmpty(_cachedDataPath))
            {
                // Without a machine identity the key collides with the same path on other machines,
                // and the server will keep evicting one of them. Say so instead of failing silently.
                McpLog.Warn("Could not read this machine's identity; HTTP Remote falls back to the project path hash, "
                    + "so Editors at the same path on other machines may disconnect each other.");
                return projectHash;
            }

            return ComputeRemoteRoutingKey(_cachedMachineIdentity, _cachedDataPath);
        }

        internal static string ComputeRemoteRoutingKey(string machineIdentity, string dataPath)
        {
            return ComputeProjectHash(machineIdentity + "\n" + dataPath);
        }

        /// <summary>
        /// Returns a human friendly project name derived from the Assets directory path,
        /// or "Unknown" if the name cannot be determined.
        /// </summary>
        public static string GetProjectName()
        {
            EnsureIdentityCache();
            return _cachedProjectName;
        }

        /// <summary>
        /// Returns the project's PlayerSettings.productGUID as 32 hex digits, or an empty string
        /// when unavailable. It lives in version-controlled ProjectSettings, so every clone of a
        /// project reports the same value whatever its folder name or machine.
        /// </summary>
        public static string GetProjectGuid()
        {
            EnsureIdentityCache();
            return _cachedProjectGuid;
        }

        /// <summary>
        /// Returns the label this machine reports to the MCP server so agents can tell machines apart.
        /// EditorPrefs are per machine and shared by every project, so one setting covers all projects.
        /// Defaults to the computer name. Changing it does not change the routing key.
        /// </summary>
        public static string GetHostLabel()
        {
            string custom = GetCustomHostLabel();
            return string.IsNullOrEmpty(custom) ? GetDefaultHostLabel() : custom;
        }

        /// <summary>
        /// Returns the host label the user set, or an empty string when unset.
        /// </summary>
        public static string GetCustomHostLabel()
        {
            try
            {
                return NormalizeHostLabel(EditorPrefs.GetString(EditorPrefKeys.HostLabel, string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Persists the host label. An empty value restores the computer name.
        /// Takes effect the next time the transport connects.
        /// </summary>
        public static void SetHostLabel(string hostLabel)
        {
            string normalized = NormalizeHostLabel(hostLabel);
            if (string.IsNullOrEmpty(normalized))
            {
                EditorPrefs.DeleteKey(EditorPrefKeys.HostLabel);
            }
            else
            {
                EditorPrefs.SetString(EditorPrefKeys.HostLabel, normalized);
            }
        }

        internal static string NormalizeHostLabel(string hostLabel)
        {
            return string.IsNullOrWhiteSpace(hostLabel) ? string.Empty : hostLabel.Trim();
        }

        internal static string GetDefaultHostLabel()
        {
            try
            {
                string machineName = Environment.MachineName;
                return string.IsNullOrEmpty(machineName) ? "Unknown" : machineName;
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string ReadProjectGuid()
        {
            try
            {
                Guid guid = PlayerSettings.productGUID;
                return guid == Guid.Empty ? string.Empty : guid.ToString("N");
            }
            catch
            {
                return string.Empty;
            }
        }

        // Both parts take part so two machines only collide when they share hardware ids and a
        // computer name; either one alone can repeat (cloned images, renamed hosts).
        private static string ReadMachineIdentity()
        {
            string deviceId = string.Empty;
            try
            {
                deviceId = SystemInfo.deviceUniqueIdentifier;
                if (deviceId == SystemInfo.unsupportedIdentifier)
                {
                    deviceId = string.Empty;
                }
            }
            catch
            {
                // Keep the computer name alone
            }

            string machineName = string.Empty;
            try
            {
                machineName = Environment.MachineName ?? string.Empty;
            }
            catch
            {
                // Keep the device id alone
            }

            return string.IsNullOrEmpty(deviceId) && string.IsNullOrEmpty(machineName)
                ? string.Empty
                : deviceId + "|" + machineName;
        }

        private static string ComputeProjectHash(string dataPath)
        {
            try
            {
                using SHA1 sha1 = SHA1.Create();
                byte[] bytes = Encoding.UTF8.GetBytes(dataPath);
                byte[] hashBytes = sha1.ComputeHash(bytes);
                var sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString(0, Math.Min(16, sb.Length)).ToLowerInvariant();
            }
            catch
            {
                return "default";
            }
        }

        private static string ComputeProjectName(string dataPath)
        {
            try
            {
                string projectPath = dataPath;
                projectPath = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (projectPath.EndsWith("Assets", StringComparison.OrdinalIgnoreCase))
                {
                    projectPath = projectPath[..^6].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                string name = Path.GetFileName(projectPath);
                return string.IsNullOrEmpty(name) ? "Unknown" : name;
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Persists a server-assigned session id.
        /// Safe to call from background threads.
        /// </summary>
        public static void SetSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    string projectHash = GetProjectHash();
                    string projectSpecificKey = $"{SessionPrefKey}_{projectHash}";
                    EditorPrefs.SetString(projectSpecificKey, sessionId);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Failed to persist session ID: {ex.Message}");
                }
            };
        }

        /// <summary>
        /// Retrieves a persistent session id for the plugin, creating one if absent.
        /// The session id is unique per project (scoped by project hash).
        /// </summary>
        public static string GetOrCreateSessionId()
        {
            try
            {
                // Make the session ID project-specific by including the project hash in the key
                string projectHash = GetProjectHash();
                string projectSpecificKey = $"{SessionPrefKey}_{projectHash}";

                string sessionId = EditorPrefs.GetString(projectSpecificKey, string.Empty);
                if (string.IsNullOrEmpty(sessionId))
                {
                    sessionId = Guid.NewGuid().ToString();
                    EditorPrefs.SetString(projectSpecificKey, sessionId);
                }
                return sessionId;
            }
            catch
            {
                // If prefs are unavailable (e.g. during batch tests) fall back to runtime guid.
                if (string.IsNullOrEmpty(_fallbackSessionId))
                {
                    _fallbackSessionId = Guid.NewGuid().ToString();
                }

                return _fallbackSessionId;
            }
        }

        /// <summary>
        /// Clears the persisted session id (mainly for tests).
        /// </summary>
        public static void ResetSessionId()
        {
            try
            {
                // Clear the project-specific session ID
                string projectHash = GetProjectHash();
                string projectSpecificKey = $"{SessionPrefKey}_{projectHash}";

                if (EditorPrefs.HasKey(projectSpecificKey))
                {
                    EditorPrefs.DeleteKey(projectSpecificKey);
                }

                if (!_legacyKeyCleared && EditorPrefs.HasKey(SessionPrefKey))
                {
                    EditorPrefs.DeleteKey(SessionPrefKey);
                    _legacyKeyCleared = true;
                }

                _fallbackSessionId = null;
            }
            catch
            {
                // Ignore
            }
        }

        private static void EnsureIdentityCache()
        {
            // When Application.dataPath is unavailable (e.g., batch mode) we fall back to
            // hashing the current working directory/Assets path so each project still
            // derives a deterministic, per-project session id rather than sharing "default".
            if (!string.IsNullOrEmpty(_cachedProjectHash) && _cachedProjectHash != "default")
            {
                return;
            }

            UpdateIdentityCache();

            if (!string.IsNullOrEmpty(_cachedProjectHash) && _cachedProjectHash != "default")
            {
                return;
            }

            string fallback = TryComputeFallbackProjectHash();
            if (!string.IsNullOrEmpty(fallback))
            {
                _cachedProjectHash = fallback;
            }
        }

        private static string TryComputeFallbackProjectHash()
        {
            try
            {
                string workingDirectory = Directory.GetCurrentDirectory();
                if (string.IsNullOrEmpty(workingDirectory))
                {
                    return "default";
                }

                // Normalise trailing separators so hashes remain stable
                workingDirectory = workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return ComputeProjectHash(Path.Combine(workingDirectory, "Assets"));
            }
            catch
            {
                return "default";
            }
        }
    }
}
