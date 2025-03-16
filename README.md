# Performance Tracker for 7 Days To Die

A powerful mod that tracks the performance impact of game methods and other mods in 7 Days To Die. Compatible with Easy Anti-Cheat (EAC).

## Overview

Performance Tracker helps you identify performance bottlenecks in your game, whether they're caused by the vanilla game or by mods. It monitors method execution times, memory usage, frame rates, and other performance metrics, giving you detailed insights into what's impacting your game's performance.

## Features

- **Real-time Performance Monitoring**: Track method execution times, memory usage, and frame rates during gameplay
- **Mod-aware Analysis**: Identify which mods are causing performance issues
- **Lag Spike Detection**: Get alerts when methods take longer than a specified threshold to execute
- **Detailed Reports**: Generate comprehensive reports showing the most performance-intensive methods and mods
- **Performance Logging**: Automatically save performance data to CSV files for later analysis
- **EAC Compatible**: Works with Easy Anti-Cheat enabled, unlike most profiling tools
- **Console Commands**: Control the tracker and view reports through in-game console commands

## Installation

1. Extract the mod archive into your 7 Days To Die `Mods` folder
2. The folder structure should look like:
   ```
   <7DaysToDie>/Mods/PerformanceTracker/
   ```
3. Start the game and wait for the mod to initialize
4. Performance tracking will begin automatically

## Commands

Access these commands through the game console (F1):

### perftracker

Main command for controlling the performance tracker:

- `perftracker enable` - Enable performance tracking
- `perftracker disable` - Disable performance tracking
- `perftracker report` - Display a comprehensive performance report in the console
- `perftracker clear` - Clear all collected performance data
- `perftracker logs` - List available performance log files
- `perftracker interval <seconds>` - Set how often performance reports are automatically generated (default: 60 seconds)

### perfalert

Command for controlling performance alerts:

- `perfalert` - Show the current alert threshold
- `perfalert <threshold_ms>` - Set the alert threshold in milliseconds (default: 16ms)

## Performance Metrics Tracked

The mod tracks the following metrics:

### Method Performance
- **Execution Time**: How long each method takes to execute (in milliseconds)
- **Call Count**: How many times each method is called
- **Performance Impact**: The total time a method consumes per frame

### Mod Performance
- **Total Impact**: The total performance impact of each mod
- **Worst Methods**: The most performance-intensive method in each mod
- **Recent Lag Spikes**: A history of the most significant performance issues

### System Metrics
- **FPS**: Current frames per second
- **Memory Usage**: Total RAM consumption (in MB)
- **GPU Memory**: Estimated GPU memory usage (in MB)
- **Draw Calls**: Approximate number of draw calls per frame
- **GPU Frame Time**: Estimated time spent on GPU rendering (in ms)

## Performance Reports

When you run `perftracker report`, you'll see three main sections:

1. **Performance Overview**: General performance metrics including FPS, memory usage, and total methods tracked
2. **Top Methods**: The 10 methods with the highest execution time in the last frame
3. **Mod Performance Summary**: Performance breakdown by mod, showing which mods are using the most resources
4. **Mod Performance Impact**: Detailed impact of each mod and its worst method
5. **Recent Lag Spikes**: A list of recent performance issues with timestamps

## Performance Alerts

When a method takes longer than the alert threshold to execute, you'll receive an alert in the console with:

- The method name and which mod it belongs to
- How long it took to execute
- The total performance impact of the mod
- Other heavy methods from the same mod

This helps you quickly identify not just the problematic method but the overall context of the performance issue.

## Log Files

Performance data is automatically saved to CSV files in:
```
<7DaysToDie>/Mods/PerformanceTracker/Logs/
```

These logs contain:
- Detailed method execution times
- Mod performance summaries
- System performance metrics

The mod automatically manages log files by:
- Creating new logs when existing ones get too large (>100MB)
- Keeping only the 10 most recent log files
- Adding timestamps to help track when performance issues occurred

## Understanding the Data

### Method Names

Method names are displayed in the format: `ClassName.MethodName`

- **Vanilla** methods come from the base game
- **Mod methods** are identified by the mod name in parentheses

### Execution Times

All times are displayed in milliseconds (ms). For smooth gameplay:
- Below 16ms is ideal (60+ FPS)
- 16-33ms is acceptable (30-60 FPS)
- Above 33ms will cause noticeable lag (below 30 FPS)

## Compatibility

- Works with all versions of 7 Days To Die that support modding
- Compatible with Easy Anti-Cheat (EAC)
- Works alongside other mods (and helps you identify which ones might be causing issues)

## Troubleshooting

### Commands Not Working
- Make sure you're typing commands without any prefix (no / or !)
- Try restarting the game after installing the mod
- Look in the game's console log for any error messages related to "PerformanceTracker"
- Try typing just `perf` and pressing Tab to see if auto-completion shows the commands
- Make sure you're pressing F1 to open the correct game console

### Log Files Not Found
- The log files are now stored in the `<7DaysToDie>/Mods/PerformanceTracker/Logs/` directory
- Use `perftracker logs` command to see the list of available log files
- Check the console output when the mod initializes for the exact path

### Mod Not Loading
- Verify your mod folder structure is correct
- Check that the DLL file exists at `<7DaysToDie>/Mods/PerformanceTracker/bin/Release/PerformanceTracker.dll`
- Look for any error messages in the console when the game starts

## Notes

- The performance tracker itself has a small performance impact, but it's designed to be as lightweight as possible
- GPU metrics are estimates, as direct GPU profiling requires tools not compatible with EAC
- Very short methods may not be tracked accurately due to measurement overhead
- The first command you enter might take a moment to be recognized as the mod initializes

## License

This mod is provided as-is with no warranty. You're free to use, modify, and distribute it according to the terms of your license. 