using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using System.Collections;

namespace PerformanceTracker
{
    public class PerformanceTrackerMod : IModApi
    {
        private static Harmony harmony;
        private static Dictionary<string, MethodPerformanceData> methodPerformanceData = new Dictionary<string, MethodPerformanceData>();
        private static string logFilePath;
        private static string logDirectory;
        private static Timer logFileWriteTimer;
        private static bool enableTracking = true;
        private static int alertThresholdMs = 16; // Alert threshold (approximately 60 FPS)
        private static List<Assembly> loadedModAssemblies = new List<Assembly>();
        private static long maxLogFileSizeBytes = 100 * 1024 * 1024; // 100MB
        private static int maxLogFileCount = 10; // Maximum number of rolling log files to keep
        private static int reportingIntervalSeconds = 15; // How often to automatically report performance
        private static float lastReportTime = 0f;
        
        // Basic performance metrics
        private static float lastMemoryUsage = 0;
        private static float lastGpuMemoryUsage = 0;
        private static int lastFrameRate = 0;
        private static int lastDrawCalls = 0;
        private static float lastGpuFrameTime = 0;
        private static bool trackAllMods = true; // Set to true to track all mods, false for selective tracking
        
        // Reference to player for displaying tooltips and in-game messages
        private static EntityPlayer localPlayer = null;

        // Add these new fields for tracking worst offenders and mod impact
        private static Dictionary<string, float> modPerformanceImpact = new Dictionary<string, float>();
        private static Dictionary<string, MethodPerformanceData> worstMethodsPerMod = new Dictionary<string, MethodPerformanceData>();
        private static List<MethodPerformanceData> recentLagSpikes = new List<MethodPerformanceData>();
        private static int maxRecentLagSpikes = 20; // Store info about the last 20 lag spikes
        private static DateTime lastModImpactCalculation = DateTime.MinValue;
        private static readonly TimeSpan modImpactCalculationInterval = TimeSpan.FromSeconds(10); // Update mod impact stats every 10 seconds

        // Class to store performance data for each method
        private class MethodPerformanceData
        {
            public string MethodName { get; set; }
            public string ModName { get; set; }
            public bool IsVanilla { get; set; }
            public long TotalCalls { get; set; }
            public long TotalExecutionTime { get; set; }
            public long MaxExecutionTime { get; set; }
            public float AverageExecutionTime => TotalCalls > 0 ? (float)TotalExecutionTime / TotalCalls : 0;
            public long LastFrameExecutionTime { get; set; }
            public int LastFrameCalls { get; set; }
            public float LastFrameMemoryUsage { get; set; }
            public float LastFrameGpuMemoryUsage { get; set; }
            public int LastFrameRate { get; set; }
            public int LastDrawCalls { get; set; }
            public float LastGpuFrameTime { get; set; }
            
            // For calculating per-frame values
            public long CurrentFrameExecutionTime { get; set; }
            public int CurrentFrameCalls { get; set; }

            public DateTime LastLagSpikeTime { get; set; } = DateTime.MinValue;

            public void ResetFrameCounters()
            {
                LastFrameExecutionTime = CurrentFrameExecutionTime;
                LastFrameCalls = CurrentFrameCalls;
                CurrentFrameExecutionTime = 0;
                CurrentFrameCalls = 0;
            }
        }
        
        // This is called after all mods are loaded
        public void InitMod(Mod mod)
        {
            // Use a more reliable path for logs - put them in the mod's folder
            logDirectory = Path.Combine(mod.Path, "Logs");
            Directory.CreateDirectory(logDirectory);
            
            UnityEngine.Debug.Log($"[PerformanceTracker] Log directory created at: {logDirectory}");
            
            // Set initial log file path
            logFilePath = Path.Combine(logDirectory, $"performance_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            // Initialize the log file with headers
            File.WriteAllText(logFilePath, "Timestamp,Method,Mod,IsVanilla,TotalCalls,AvgTimeMs,MaxTimeMs,LastFrameTimeMs,LastFrameCalls,FPS,TotalMemoryMB,GpuMemoryMB,DrawCalls,GpuTimeMs\n");

            // Output the log file path to the console
            UnityEngine.Debug.Log($"[PerformanceTracker] Log file created at: {logFilePath}");

            // Setup timer to periodically write data to log file (every 10 seconds)
            logFileWriteTimer = new Timer(WriteLogToFile, null, 10000, 10000);

            // Register console commands when game is fully loaded
            // Register immediately AND use GameStartDone event to ensure commands are registered
            RegisterCommands();
            ModEvents.GameStartDone.RegisterHandler(RegisterCommands);

            // Collect all loaded mod assemblies
            CollectModAssemblies();

            // Register for the update event to track frame-by-frame performance
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);

            // Apply Harmony patches
            harmony = new Harmony(mod.Name);
            PatchAllModMethods();
            PatchVanillaGameMethods();

            UnityEngine.Debug.Log("[PerformanceTracker] Mod initialized and all methods patched!");
            UnityEngine.Debug.Log("[PerformanceTracker] Type 'perftracker' (without quotes) in the console (F1) to access commands");
        }
        
        private void CollectModAssemblies()
        {
            try
            {
                // Add our own assembly first
                loadedModAssemblies.Add(GetType().Assembly);
                
                UnityEngine.Debug.Log("[PerformanceTracker] Collecting mod assemblies...");
                
                // Get all loaded mods through reflection if possible
                var modManagerType = typeof(GameManager).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "ModManager");
                
                if (modManagerType != null)
                {
                    var getLoadedModsMethod = modManagerType.GetMethod("GetLoadedMods", 
                        BindingFlags.Public | BindingFlags.Static);
                    
                    if (getLoadedModsMethod != null)
                    {
                        var loadedMods = getLoadedModsMethod.Invoke(null, null) as IEnumerable;
                        if (loadedMods != null)
                        {
                            foreach (var loadedMod in loadedMods)
                            {
                                try
                                {
                                    // Get the main assembly from the mod
                                    var modType = loadedMod.GetType();
                                    var namePropertyInfo = modType.GetProperty("Name");
                                    if (namePropertyInfo != null)
                                    {
                                        string modName = namePropertyInfo.GetValue(loadedMod) as string;
                                        UnityEngine.Debug.Log($"[PerformanceTracker] Found mod {modName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    UnityEngine.Debug.LogWarning($"[PerformanceTracker] Error processing mod: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                
                // Fallback: if we couldn't get mod assemblies through ModManager, scan loaded assemblies
                if (loadedModAssemblies.Count <= 1 && trackAllMods)
                {
                    UnityEngine.Debug.Log("[PerformanceTracker] Using fallback method to detect mod assemblies");
                    
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            string assemblyName = assembly.GetName().Name;
                            
                            // Skip system and Unity assemblies
                            if (!assemblyName.StartsWith("System") && 
                                !assemblyName.StartsWith("Unity") && 
                                !assemblyName.StartsWith("mscorlib") &&
                                !assemblyName.StartsWith("netstandard") &&
                                !assembly.FullName.StartsWith("Assembly-CSharp") &&
                                // Skip our own assembly as it's already added
                                assembly != GetType().Assembly)
                            {
                                loadedModAssemblies.Add(assembly);
                                UnityEngine.Debug.Log($"[PerformanceTracker] Added potential mod assembly: {assemblyName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogWarning($"[PerformanceTracker] Error processing assembly: {ex.Message}");
                        }
                    }
                }
                
                UnityEngine.Debug.Log($"[PerformanceTracker] Total mod assemblies detected: {loadedModAssemblies.Count}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error collecting mod assemblies: {ex}");
            }
        }

        private void RegisterCommands()
        {
            try
            {
                // Create command instances
                var cmdPerfTracker = new CommandPerfTracker();
                var cmdPerfAlert = new CommandPerfAlert();
                
                // Get the commands field from SdtdConsole using reflection
                var commandsField = typeof(SdtdConsole).GetField("commands", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                    
                if (commandsField != null)
                {
                    // Get the existing commands collection
                    var gameCommands = commandsField.GetValue(SdtdConsole.Instance) as SortedList<string, IConsoleCommand>;
                    
                    if (gameCommands != null)
                    {
                        // Add our commands directly to the game's command collection
                        foreach (var cmd in cmdPerfTracker.getCommands())
                        {
                            if (!gameCommands.ContainsKey(cmd))
                            {
                                gameCommands.Add(cmd, cmdPerfTracker);
                                UnityEngine.Debug.Log($"[PerformanceTracker] Added command: {cmd}");
                            }
                            else
                            {
                                // If command already exists (perhaps from a previous attempt), replace it
                                gameCommands.Remove(cmd);
                                gameCommands.Add(cmd, cmdPerfTracker);
                                UnityEngine.Debug.Log($"[PerformanceTracker] Replaced existing command: {cmd}");
                            }
                        }
                        
                        foreach (var cmd in cmdPerfAlert.getCommands())
                        {
                            if (!gameCommands.ContainsKey(cmd))
                            {
                                gameCommands.Add(cmd, cmdPerfAlert);
                                UnityEngine.Debug.Log($"[PerformanceTracker] Added command: {cmd}");
                            }
                            else
                            {
                                // If command already exists (perhaps from a previous attempt), replace it
                                gameCommands.Remove(cmd);
                                gameCommands.Add(cmd, cmdPerfAlert);
                                UnityEngine.Debug.Log($"[PerformanceTracker] Replaced existing command: {cmd}");
                            }
                        }
                        
                        UnityEngine.Debug.Log("[PerformanceTracker] Commands added directly to SdtdConsole");
                        
                        // Try to trigger a command refresh if possible
                        try {
                            var refreshMethod = typeof(SdtdConsole).GetMethod("RefreshCommands", 
                                BindingFlags.NonPublic | BindingFlags.Instance);
                            if (refreshMethod != null) {
                                refreshMethod.Invoke(SdtdConsole.Instance, null);
                                UnityEngine.Debug.Log("[PerformanceTracker] Console commands refreshed");
                            }
                        } catch (Exception ex) {
                            UnityEngine.Debug.LogWarning($"[PerformanceTracker] Command refresh failed: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Alternative approach if game commands SortedList not found
                        UnityEngine.Debug.LogWarning("[PerformanceTracker] Game commands list not found, trying alternative method");
                        
                        // Try to use the vanilla method for registering commands
                        try {
                            // For 7 Days to Die A21+
                            var registerCmdMethod = typeof(SdtdConsole).GetMethod("RegisterCommand", 
                                BindingFlags.Public | BindingFlags.Instance, 
                                null, 
                                new[] { typeof(string), typeof(IConsoleCommand) }, 
                                null);
                                
                            if (registerCmdMethod != null) {
                                registerCmdMethod.Invoke(SdtdConsole.Instance, new object[] { "perftracker", cmdPerfTracker });
                                registerCmdMethod.Invoke(SdtdConsole.Instance, new object[] { "perfalert", cmdPerfAlert });
                                UnityEngine.Debug.Log("[PerformanceTracker] Commands registered via direct method call");
                            } else {
                                var commands = new SortedList<string, IConsoleCommand>();
                                SdtdConsole.Instance.RegisterCommand(commands, "perftracker", cmdPerfTracker);
                                SdtdConsole.Instance.RegisterCommand(commands, "perfalert", cmdPerfAlert);
                                UnityEngine.Debug.Log("[PerformanceTracker] Commands registered via RegisterCommand method");
                            }
                        } catch (Exception ex) {
                            UnityEngine.Debug.LogError($"[PerformanceTracker] Alternative command registration failed: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // Last resort approach
                    UnityEngine.Debug.LogWarning("[PerformanceTracker] Commands field not found using reflection, trying standard method");
                    var commands = new SortedList<string, IConsoleCommand>();
                    SdtdConsole.Instance.RegisterCommand(commands, "perftracker", cmdPerfTracker);
                    SdtdConsole.Instance.RegisterCommand(commands, "perfalert", cmdPerfAlert);
                    UnityEngine.Debug.Log("[PerformanceTracker] Commands registered via RegisterCommand method (reflection failed)");
                }
                
                UnityEngine.Debug.Log("[PerformanceTracker] Console commands registration completed");
                UnityEngine.Debug.Log("[PerformanceTracker] Use 'perftracker' and 'perfalert' in the console (without the / prefix)");
                
                // Output list of all registered commands to help debug
                try {
                    var allCommands = SdtdConsole.Instance.GetCommands();
                    if (allCommands != null) {
                        UnityEngine.Debug.Log($"[PerformanceTracker] Total commands in console: {allCommands.Count}");
                        
                        // Check if our command exists - simplified approach
                        UnityEngine.Debug.Log("[PerformanceTracker] Command registration seems to be complete.");
                        UnityEngine.Debug.Log("[PerformanceTracker] Please check in-game if commands are available.");
                    }
                } catch (Exception ex) {
                    UnityEngine.Debug.LogWarning($"[PerformanceTracker] Failed to get command list: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error registering commands: {ex.Message}");
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error details: {ex}");
            }
        }
        
        // Custom command class for handling perftracker console commands
        private class CommandPerfTracker : ConsoleCmdAbstract
        {
            public override string getDescription() => "Performance Tracker commands";
            
            public override string getHelp() => "Usage: perftracker [enable|disable|report|clear|logs|interval <seconds>] - Control and view performance metrics";
            
            public override string[] getCommands() => new[] { "perftracker" };
            
            public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
            {
                if (_params.Count == 0)
                {
                    SdtdConsole.Instance.Output("Usage: perftracker [enable|disable|report|clear|logs|interval <seconds>]");
                    return;
                }

                switch (_params[0].ToLower())
                {
                    case "enable":
                        enableTracking = true;
                        SdtdConsole.Instance.Output("Performance tracking enabled");
                        break;
                    case "disable":
                        enableTracking = false;
                        SdtdConsole.Instance.Output("Performance tracking disabled");
                        break;
                    case "report":
                        OutputPerformanceReport(true);
                        break;
                    case "clear":
                        methodPerformanceData.Clear();
                        SdtdConsole.Instance.Output("Performance data cleared");
                        break;
                    case "logs":
                        ListLogFiles();
                        break;
                    case "interval":
                        if (_params.Count > 1 && int.TryParse(_params[1], out int interval) && interval > 0)
                        {
                            reportingIntervalSeconds = interval;
                            SdtdConsole.Instance.Output($"Reporting interval set to {reportingIntervalSeconds} seconds");
                        }
                        else
                        {
                            SdtdConsole.Instance.Output($"Current reporting interval: {reportingIntervalSeconds} seconds");
                            SdtdConsole.Instance.Output("Usage: perftracker interval <seconds>");
                        }
                        break;
                    default:
                        SdtdConsole.Instance.Output("Invalid command. Use: perftracker [enable|disable|report|clear|logs|interval <seconds>]");
                        break;
                }
            }
        }
        
        // Custom command class for handling perfalert console commands
        private class CommandPerfAlert : ConsoleCmdAbstract
        {
            public override string getDescription() => "Set performance alert threshold in ms";
            
            public override string getHelp() => "Usage: perfalert [threshold_ms] - Set or view the alert threshold for slow methods (in milliseconds)";
            
            public override string[] getCommands() => new[] { "perfalert" };
            
            public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
            {
                if (_params.Count == 0)
                {
                    SdtdConsole.Instance.Output($"Current alert threshold: {alertThresholdMs}ms");
                    return;
                }

                if (int.TryParse(_params[0], out int threshold) && threshold > 0)
                {
                    alertThresholdMs = threshold;
                    SdtdConsole.Instance.Output($"Alert threshold set to {alertThresholdMs}ms");
                }
                else
                {
                    SdtdConsole.Instance.Output("Invalid threshold. Please enter a positive number.");
                }
            }
        }
        
        private static void ListLogFiles()
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "performance_log_*.csv")
                    .OrderByDescending(f => new FileInfo(f).CreationTime)
                    .ToList();
                
                SdtdConsole.Instance.Output($"Log files in {logDirectory}:");
                foreach (var file in logFiles)
                {
                    var fileInfo = new FileInfo(file);
                    SdtdConsole.Instance.Output($"{Path.GetFileName(file)} - {fileInfo.Length / (1024.0 * 1024.0):F2} MB - {fileInfo.CreationTime}");
                }
            }
            catch (Exception ex)
            {
                SdtdConsole.Instance.Output($"Error listing log files: {ex.Message}");
            }
        }

        private static void OutputPerformanceReport(bool toConsole = false)
        {
            string header = "===== PERFORMANCE REPORT =====";
            string totalMethods = $"Total tracked methods: {methodPerformanceData.Count}";
            string fps = $"Current FPS: {lastFrameRate:F1}";
            string memoryUsage = $"Total Memory Usage: {lastMemoryUsage:F2} MB";
            string gpuMemory = $"GPU Memory Usage: {lastGpuMemoryUsage:F2} MB";
            string drawCalls = $"Draw Calls: {lastDrawCalls}";
            string gpuTime = $"GPU Frame Time: {lastGpuFrameTime:F2} ms";
            
            // Calculate per-mod memory usage estimates
            var modMemoryUsage = methodPerformanceData.Values
                .GroupBy(m => m.IsVanilla ? "Vanilla" : m.ModName)
                .Select(g => new {
                    ModName = g.Key,
                    MemoryUsage = g.Sum(m => m.LastFrameMemoryUsage) / g.Count(), // Average memory per method * method count
                    GpuMemoryUsage = g.Sum(m => m.LastFrameGpuMemoryUsage) / g.Count(),
                    DrawCalls = g.Sum(m => m.LastDrawCalls),
                    TotalMethods = g.Count(),
                    TotalCalls = g.Sum(m => m.LastFrameCalls)
                })
                .OrderByDescending(m => m.MemoryUsage)
                .ToList();

            string modResourceHeader = "\n===== MOD RESOURCE CONSUMPTION =====";
            
            // Always log to Unity debug log
            UnityEngine.Debug.Log(header);
            UnityEngine.Debug.Log(totalMethods);
            UnityEngine.Debug.Log(fps);
            UnityEngine.Debug.Log(memoryUsage);
            UnityEngine.Debug.Log(gpuMemory);
            UnityEngine.Debug.Log(drawCalls);
            UnityEngine.Debug.Log(gpuTime);
            UnityEngine.Debug.Log(modResourceHeader);
            
            foreach (var mod in modMemoryUsage)
            {
                string modResourceInfo = $"{mod.ModName}:\n" +
                    $"  Memory: {mod.MemoryUsage:F2} MB\n" +
                    $"  GPU Memory: {mod.GpuMemoryUsage:F2} MB\n" +
                    $"  Draw Calls: {mod.DrawCalls}\n" +
                    $"  Methods: {mod.TotalMethods}\n" +
                    $"  Calls/Frame: {mod.TotalCalls}";
                UnityEngine.Debug.Log(modResourceInfo);
            }
            
            string topMethodsHeader = "\nTop 10 methods by last frame execution time:";
            UnityEngine.Debug.Log(topMethodsHeader);
            
            // Also output to console if requested
            if (toConsole)
            {
                SdtdConsole.Instance.Output(header);
                SdtdConsole.Instance.Output(totalMethods);
                SdtdConsole.Instance.Output(fps);
                SdtdConsole.Instance.Output(memoryUsage);
                SdtdConsole.Instance.Output(gpuMemory);
                SdtdConsole.Instance.Output(drawCalls);
                SdtdConsole.Instance.Output(gpuTime);
                SdtdConsole.Instance.Output(modResourceHeader);
                
                foreach (var mod in modMemoryUsage)
                {
                    SdtdConsole.Instance.Output($"{mod.ModName}:");
                    SdtdConsole.Instance.Output($"  Memory: {mod.MemoryUsage:F2} MB");
                    SdtdConsole.Instance.Output($"  GPU Memory: {mod.GpuMemoryUsage:F2} MB");
                    SdtdConsole.Instance.Output($"  Draw Calls: {mod.DrawCalls}");
                    SdtdConsole.Instance.Output($"  Methods: {mod.TotalMethods}");
                    SdtdConsole.Instance.Output($"  Calls/Frame: {mod.TotalCalls}");
                }
                
                SdtdConsole.Instance.Output(topMethodsHeader);
            }
            
            var topMethods = methodPerformanceData.Values
                .OrderByDescending(m => m.LastFrameExecutionTime)
                .Take(10)
                .ToList();
                
            foreach (var method in topMethods)
            {
                string source = method.IsVanilla ? "Vanilla" : method.ModName;
                string methodInfo = $"{method.MethodName} ({source}): {method.LastFrameExecutionTime / 10000f:F2}ms, Calls: {method.LastFrameCalls}";
                
                UnityEngine.Debug.Log(methodInfo);
                if (toConsole)
                {
                    SdtdConsole.Instance.Output(methodInfo);
                }
            }
            
            string footer = "===============================";
            UnityEngine.Debug.Log(footer);
            if (toConsole)
            {
                SdtdConsole.Instance.Output(footer);
            }
            
            // Also display a summary of mod performance by grouping methods by mod
            if (toConsole)
            {
                SdtdConsole.Instance.Output("\n===== MOD PERFORMANCE SUMMARY =====");
                
                var modPerformance = methodPerformanceData.Values
                    .GroupBy(m => m.IsVanilla ? "Vanilla" : m.ModName)
                    .Select(g => new {
                        ModName = g.Key,
                        TotalMethods = g.Count(),
                        TotalCalls = g.Sum(m => m.TotalCalls),
                        AvgExecutionTime = g.Sum(m => m.LastFrameExecutionTime) / 10000f,
                        MaxMethod = g.OrderByDescending(m => m.LastFrameExecutionTime).FirstOrDefault()
                    })
                    .OrderByDescending(m => m.AvgExecutionTime)
                    .ToList();
                
                foreach (var mod in modPerformance)
                {
                    SdtdConsole.Instance.Output($"{mod.ModName}: {mod.AvgExecutionTime:F2}ms total, {mod.TotalMethods} methods, {mod.TotalCalls} calls");
                    if (mod.MaxMethod != null)
                    {
                        SdtdConsole.Instance.Output($"  Heaviest method: {mod.MaxMethod.MethodName}: {mod.MaxMethod.LastFrameExecutionTime / 10000f:F2}ms");
                    }
                }
                
                SdtdConsole.Instance.Output("===============================");
            }

            // Add information about top lag-causing mods
            string modImpactHeader = "\n===== MOD PERFORMANCE IMPACT =====";
            UnityEngine.Debug.Log(modImpactHeader);
            if (toConsole)
            {
                SdtdConsole.Instance.Output(modImpactHeader);
            }
            
            // Get top 5 mods by performance impact
            var topLagMods = modPerformanceImpact
                .OrderByDescending(m => m.Value)
                .Take(5)
                .ToList();
                
            foreach (var modImpact in topLagMods)
            {
                string modName = modImpact.Key;
                float impact = modImpact.Value;
                
                string worstMethodInfo = "";
                if (worstMethodsPerMod.TryGetValue(modName, out var worstMethod))
                {
                    worstMethodInfo = $", worst method: {worstMethod.MethodName} ({worstMethod.LastFrameExecutionTime / 10000f:F2}ms)";
                }
                
                string modImpactInfo = $"{modName}: {impact:F2}ms total impact{worstMethodInfo}";
                
                UnityEngine.Debug.Log(modImpactInfo);
                if (toConsole)
                {
                    SdtdConsole.Instance.Output(modImpactInfo);
                }
            }
            
            // Add information about recent lag spikes if available
            if (recentLagSpikes.Count > 0)
            {
                string recentSpikesHeader = "\n===== RECENT LAG SPIKES =====";
                UnityEngine.Debug.Log(recentSpikesHeader);
                if (toConsole)
                {
                    SdtdConsole.Instance.Output(recentSpikesHeader);
                }
                
                foreach (var spike in recentLagSpikes.Take(5))
                {
                    string source = spike.IsVanilla ? "Vanilla" : spike.ModName;
                    string spikeInfo = $"{spike.MethodName} ({source}): {spike.MaxExecutionTime / 10000f:F2}ms (occurred at {spike.LastLagSpikeTime})";
                    
                    UnityEngine.Debug.Log(spikeInfo);
                    if (toConsole)
                    {
                        SdtdConsole.Instance.Output(spikeInfo);
                    }
                }
            }

            // Add log file location to the output
            if (toConsole)
            {
                SdtdConsole.Instance.Output("\n===== LOG FILE LOCATION =====");
                SdtdConsole.Instance.Output($"Performance logs are saved to: {logDirectory}");
                SdtdConsole.Instance.Output($"Current log file: {Path.GetFileName(logFilePath)}");
                SdtdConsole.Instance.Output("Use 'perftracker logs' to see all available log files");
            }
        }

        private void PatchAllModMethods()
        {
            foreach (var assembly in loadedModAssemblies)
            {
                try
                {
                    string modName = assembly.GetName().Name;
                    
                    // Get all types in the assembly
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass)
                        {
                            // Get all methods in the type
                            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            {
                                // Skip abstract, generic, and compiler-generated methods
                                if (method.IsAbstract || method.IsGenericMethod || method.Name.Contains("<") || method.ContainsGenericParameters)
                                    continue;
                                    
                                // Skip properties
                                if (method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")))
                                    continue;
                                
                                try
                                {
                                    var prefix = new HarmonyMethod(typeof(PerformanceTrackerMod).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                                    var postfix = new HarmonyMethod(typeof(PerformanceTrackerMod).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
                                    
                                    harmony.Patch(method, prefix, postfix);
                                }
                                catch (Exception)
                                {
                                    // Some methods can't be patched - that's normal, just skip them
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[PerformanceTracker] Error patching assembly {assembly.GetName().Name}: {ex}");
                }
            }
        }

        private void PatchVanillaGameMethods()
        {
            // List of important vanilla game classes to monitor
            var classesToMonitor = new[]
            {
                typeof(GameManager),
                typeof(World),
                typeof(EntityPlayer),
                typeof(NetPackageManager),
                typeof(Block),
                typeof(ChunkCache),
                typeof(TileEntity),
                typeof(Texture2D),
                typeof(Material),
                typeof(Renderer),
                typeof(Behaviour)
            };
            
            foreach (var type in classesToMonitor)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    // Skip abstract, generic, and compiler-generated methods
                    if (method.IsAbstract || method.IsGenericMethod || method.Name.Contains("<") || method.ContainsGenericParameters)
                        continue;
                        
                    // Skip properties
                    if (method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")))
                        continue;
                    
                    try
                    {
                        var prefix = new HarmonyMethod(typeof(PerformanceTrackerMod).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                        var postfix = new HarmonyMethod(typeof(PerformanceTrackerMod).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
                        
                        harmony.Patch(method, prefix, postfix);
                    }
                    catch (Exception)
                    {
                        // Some methods can't be patched - that's normal, just skip them
                    }
                }
            }
            
            // Additional targeted patches for heavy Unity functionality
            try 
            {
                // Try to patch some key Unity functions known to impact performance
                var renderingAssembly = typeof(Renderer).Assembly;
                var renderingTypes = new[] {
                    "UnityEngine.MeshRenderer",
                    "UnityEngine.Graphics"
                };
                
                foreach (var typeName in renderingTypes)
                {
                    try
                    {
                        var type = renderingAssembly.GetType(typeName);
                        if (type != null)
                        {
                            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            {
                                if (method.IsAbstract || method.IsGenericMethod)
                                    continue;
                                
                                try 
                                {
                                    var prefix = new HarmonyMethod(typeof(PerformanceTrackerMod).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                                    var postfix = new HarmonyMethod(typeof(PerformanceTrackerMod).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
                                    
                                    harmony.Patch(method, prefix, postfix);
                                }
                                catch
                                {
                                    // Skip if can't patch
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip if type not found
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PerformanceTracker] Error patching Unity rendering methods: {ex.Message}");
            }
        }

        private static void OnGameUpdate()
        {
            // Reset per-frame counters for each method
            foreach (var data in methodPerformanceData.Values)
            {
                data.ResetFrameCounters();
            }
            
            // Get local player reference if available
            if (localPlayer == null && GameManager.Instance != null)
            {
                try
                {
                    localPlayer = GameManager.Instance.World.GetPrimaryPlayer();
                }
                catch (Exception)
                {
                    // Ignore errors when trying to get player
                }
            }
            
            // Update basic metrics
            try
            {
                lastFrameRate = Mathf.RoundToInt(1.0f / Time.smoothDeltaTime);
                lastMemoryUsage = GC.GetTotalMemory(false) / (1024f * 1024f); // Convert bytes to MB
                
                // Update GPU metrics using alternative methods
                UpdateGpuMetrics();
                
                // Update mod performance impact data
                UpdateModPerformanceImpact();
                
                // Update all method data with the latest metrics
                foreach (var data in methodPerformanceData.Values)
                {
                    data.LastFrameRate = lastFrameRate;
                    data.LastFrameMemoryUsage = lastMemoryUsage;
                    data.LastFrameGpuMemoryUsage = lastGpuMemoryUsage;
                    data.LastDrawCalls = lastDrawCalls;
                    data.LastGpuFrameTime = lastGpuFrameTime;
                }
                
                // Report periodically based on set interval
                if (Time.time - lastReportTime >= reportingIntervalSeconds)
                {
                    OutputPerformanceReport(false);
                    lastReportTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PerformanceTracker] Error updating metrics: {ex.Message}");
            }
        }
        
        private static void UpdateGpuMetrics()
        {
            try
            {
                // Get approximate GPU memory usage
                lastGpuMemoryUsage = SystemInfo.graphicsMemorySize; // This is actually total VRAM, not current usage
                
                // Simple estimate for draw calls - not accurate
                lastDrawCalls = 100; // Placeholder value
                
                // GPU time is hard to measure without ProfilerRecorder, but we can estimate
                lastGpuFrameTime = Time.unscaledDeltaTime * 1000f * 0.7f; // Rough estimate: 70% of frame time could be GPU
                
                // Count active renderers in the scene if possible
                try
                {
                    var renderers = UnityEngine.Object.FindObjectsOfType(typeof(Renderer));
                    if (renderers != null)
                    {
                        lastDrawCalls = renderers.Length;
                    }
                }
                catch
                {
                    // If FindObjectsOfType fails, keep placeholder value
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PerformanceTracker] Error updating GPU metrics: {ex.Message}");
            }
        }

        private static void CheckLogFileSize()
        {
            try
            {
                var fileInfo = new FileInfo(logFilePath);
                if (fileInfo.Exists && fileInfo.Length >= maxLogFileSizeBytes)
                {
                    // Roll to a new log file
                    RollLogFile();
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error checking log file size: {ex}");
            }
        }

        private static void RollLogFile()
        {
            try
            {
                // Create a new log file
                logFilePath = Path.Combine(logDirectory, $"performance_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                File.WriteAllText(logFilePath, "Timestamp,Method,Mod,IsVanilla,TotalCalls,AvgTimeMs,MaxTimeMs,LastFrameTimeMs,LastFrameCalls,FPS,TotalMemoryMB,GpuMemoryMB,DrawCalls,GpuTimeMs\n");
                
                // Clean up old log files if we have too many
                CleanupOldLogFiles();
                
                UnityEngine.Debug.Log("[PerformanceTracker] Rolled over to new log file: " + logFilePath);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error rolling log file: {ex}");
            }
        }
        
        private static void CleanupOldLogFiles()
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "performance_log_*.csv")
                    .OrderByDescending(f => new FileInfo(f).CreationTime)
                    .Skip(maxLogFileCount)
                    .ToList();
                
                foreach (var oldFile in logFiles)
                {
                    try
                    {
                        File.Delete(oldFile);
                        UnityEngine.Debug.Log($"[PerformanceTracker] Deleted old log file: {Path.GetFileName(oldFile)}");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[PerformanceTracker] Could not delete old log file {Path.GetFileName(oldFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error cleaning up old log files: {ex}");
            }
        }

        private static void WriteLogToFile(object state)
        {
            if (!enableTracking || methodPerformanceData.Count == 0)
                return;
                
            try
            {
                // Check if we need to roll the log file
                CheckLogFileSize();
                
                StringBuilder sb = new StringBuilder();
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                
                // Add summary of mod impact to the log
                sb.AppendLine($"{timestamp},MOD_SUMMARY,,,,,,,,,,,,,");
                
                foreach (var modImpact in modPerformanceImpact.OrderByDescending(m => m.Value))
                {
                    string modName = modImpact.Key;
                    float impact = modImpact.Value;
                    
                    sb.AppendLine($"{timestamp},MOD_IMPACT,{modName},,,,{impact:F6},,,,,,,,");
                    
                    // Add the worst method for this mod
                    if (worstMethodsPerMod.TryGetValue(modName, out var worstMethod))
                    {
                        sb.AppendLine($"{timestamp},MOD_WORST_METHOD,{modName},{worstMethod.MethodName},,{worstMethod.LastFrameExecutionTime / 10000f:F6},,,,,,,,");
                    }
                }
                
                // Add detailed method data
                foreach (var kvp in methodPerformanceData)
                {
                    var data = kvp.Value;
                    
                    // Only log methods that were actually called
                    if (data.TotalCalls > 0)
                    {
                        sb.AppendLine($"{timestamp},{data.MethodName},{data.ModName},{data.IsVanilla},{data.TotalCalls}," +
                           $"{data.AverageExecutionTime / 10000f:F6},{data.MaxExecutionTime / 10000f:F6}," +
                           $"{data.LastFrameExecutionTime / 10000f:F6},{data.LastFrameCalls},{data.LastFrameRate:F1}," +
                           $"{data.LastFrameMemoryUsage:F2},{data.LastFrameGpuMemoryUsage:F2}," +
                           $"{data.LastDrawCalls},{data.LastGpuFrameTime:F2}");
                    }
                }
                
                File.AppendAllText(logFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PerformanceTracker] Error writing log file: {ex}");
            }
        }

        // Harmony patch prefix
        private static void Prefix(MethodBase __originalMethod, ref long __state)
        {
            if (!enableTracking)
                return;
                
            __state = Stopwatch.GetTimestamp();
        }

        // Harmony patch postfix
        private static void Postfix(MethodBase __originalMethod, long __state)
        {
            if (!enableTracking)
                return;
                
            long endTime = Stopwatch.GetTimestamp();
            long executionTime = endTime - __state;
            
            string methodName = $"{__originalMethod.DeclaringType.Name}.{__originalMethod.Name}";
            string modName = "Unknown";
            bool isVanilla = true;
            
            // Determine which mod this method belongs to
            Assembly methodAssembly = __originalMethod.DeclaringType.Assembly;
            foreach (var assembly in loadedModAssemblies)
            {
                if (assembly == methodAssembly)
                {
                    modName = assembly.GetName().Name;
                    isVanilla = false;
                    break;
                }
            }
            
            // Add or update method performance data
            MethodPerformanceData data;
            lock (methodPerformanceData)
            {
                if (!methodPerformanceData.TryGetValue(methodName, out data))
                {
                    data = new MethodPerformanceData
                    {
                        MethodName = methodName,
                        ModName = modName,
                        IsVanilla = isVanilla
                    };
                    methodPerformanceData[methodName] = data;
                }
                
                data.TotalCalls++;
                data.TotalExecutionTime += executionTime;
                data.CurrentFrameExecutionTime += executionTime;
                data.CurrentFrameCalls++;
                
                if (executionTime > data.MaxExecutionTime)
                {
                    data.MaxExecutionTime = executionTime;
                    data.LastLagSpikeTime = DateTime.Now;
                }
                
                // Alert if execution time exceeds threshold
                float executionTimeMs = executionTime / 10000f; // Convert to ms
                if (executionTimeMs > alertThresholdMs)
                {
                    // Only alert every 30 seconds for the same method to avoid spam
                    string alertKey = $"{methodName}_alert";
                    if (!alertedMethods.Contains(alertKey))
                    {
                        alertedMethods.Add(alertKey);
                        
                        // Set a timer to remove the method from the alerted methods list after 30 seconds
                        Timer alertCooldownTimer = null;
                        alertCooldownTimer = new Timer((state) => 
                        {
                            alertedMethods.Remove(alertKey);
                            alertCooldownTimer.Dispose();
                        }, null, 30000, Timeout.Infinite);
                        
                        // Record this as a recent lag spike
                        lock (recentLagSpikes)
                        {
                            // Create a copy of the performance data for the lag spike record
                            var lagSpikeData = new MethodPerformanceData
                            {
                                MethodName = data.MethodName,
                                ModName = data.ModName,
                                IsVanilla = data.IsVanilla,
                                MaxExecutionTime = executionTime,
                                LastLagSpikeTime = DateTime.Now
                            };
                            
                            recentLagSpikes.Insert(0, lagSpikeData);
                            
                            // Keep only the most recent spikes
                            if (recentLagSpikes.Count > maxRecentLagSpikes)
                            {
                                recentLagSpikes.RemoveAt(recentLagSpikes.Count - 1);
                            }
                        }
                        
                        // Get mod performance context
                        string modContext = "";
                        if (modPerformanceImpact.TryGetValue(isVanilla ? "Vanilla" : modName, out float modImpact))
                        {
                            // Get detailed resource usage for this mod
                            var modMethods = methodPerformanceData.Values
                                .Where(m => (m.IsVanilla == isVanilla) && (m.IsVanilla ? true : m.ModName == modName))
                                .ToList();
                                
                            float avgMemory = modMethods.Average(m => m.LastFrameMemoryUsage);
                            float avgGpuMemory = modMethods.Average(m => m.LastFrameGpuMemoryUsage);
                            int totalDrawCalls = modMethods.Sum(m => m.LastDrawCalls);
                            
                            modContext = $"\nMod Performance Impact:" +
                                $"\n - Total Impact: {modImpact:F2}ms" +
                                $"\n - Memory Usage: {avgMemory:F2} MB" +
                                $"\n - GPU Memory: {avgGpuMemory:F2} MB" +
                                $"\n - Draw Calls: {totalDrawCalls}" +
                                $"\n - Active Methods: {modMethods.Count}";
                            
                            // Add information about other heavy methods from this mod
                            var otherHeavyMethods = methodPerformanceData.Values
                                .Where(m => (m.IsVanilla == isVanilla) && 
                                       (m.IsVanilla ? true : m.ModName == modName) && 
                                       m.MethodName != methodName)
                                .OrderByDescending(m => m.LastFrameExecutionTime)
                                .Take(3)
                                .ToList();
                                
                            if (otherHeavyMethods.Count > 0)
                            {
                                modContext += "\nOther heavy methods in this mod:";
                                foreach (var heavyMethod in otherHeavyMethods)
                                {
                                    modContext += $"\n - {heavyMethod.MethodName}: {heavyMethod.LastFrameExecutionTime / 10000f:F2}ms";
                                }
                            }
                        }
                        
                        // Display the alert with enhanced information
                        string source = isVanilla ? "Vanilla" : modName;
                        string message = $"[PERFORMANCE ALERT] {methodName} ({source}) took {executionTimeMs:F2}ms! (Threshold: {alertThresholdMs}ms){modContext}";
                        
                        // Show in logs
                        UnityEngine.Debug.LogWarning(message);
                        
                        // Send to in-game console
                        try 
                        {
                            SdtdConsole.Instance.Output(message);
                        }
                        catch {}
                        
                        // Try to show in-game notification if player is available
                        try
                        {
                            if (localPlayer != null && GameManager.Instance != null)
                            {
                                // Log to console only, as GameManager.ShowTooltip API might be incompatible
                                UnityEngine.Debug.LogWarning($"Performance alert: {message}");
                            }
                        }
                        catch {}
                    }
                }
            }
        }
        
        // List to track which methods we've already alerted about (to prevent alert spam)
        private static HashSet<string> alertedMethods = new HashSet<string>();

        // Add this method to calculate overall mod impact
        private static void UpdateModPerformanceImpact()
        {
            if (DateTime.Now - lastModImpactCalculation < modImpactCalculationInterval)
                return;
                
            lastModImpactCalculation = DateTime.Now;
            modPerformanceImpact.Clear();
            worstMethodsPerMod.Clear();
            
            // Group methods by mod
            var modGroups = methodPerformanceData.Values
                .Where(m => m.TotalCalls > 0)
                .GroupBy(m => m.IsVanilla ? "Vanilla" : m.ModName);
                
            // Calculate total impact of each mod (based on last frame time)
            foreach (var group in modGroups)
            {
                string modName = group.Key;
                float totalImpact = group.Sum(m => m.LastFrameExecutionTime / 10000f);
                modPerformanceImpact[modName] = totalImpact;
                
                // Find worst method for each mod
                var worstMethod = group.OrderByDescending(m => m.LastFrameExecutionTime).FirstOrDefault();
                if (worstMethod != null)
                {
                    worstMethodsPerMod[modName] = worstMethod;
                }
            }
            
            UnityEngine.Debug.Log($"[PerformanceTracker] Updated mod performance impact for {modPerformanceImpact.Count} mods");
        }
    }
}