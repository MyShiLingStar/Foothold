# MyFoothold

A smoothness and responsiveness focused alternative to the original Foothold? mod.
This is a derivative work of the original mod. 
Credit goes to the original author for the concept and implementation. 
This version maintains the same license and is distributed as a community alternative.

## Key Differences from Original

This edition prioritizes consistent frametime smoothness and responsiveness, eliminating the stutter spikes that occurred during visualization scans in the original version.
Rather than optimizing for raw computational speed, this version focuses on maintaining consistent frame rates by distributing computational work smoothly across multiple frames.

1. Intelligent pruning — Background cache management with scan prioritization
2. Smooth frame pacing — All heavy computational work is distributed across frames to prevent frametime spikes as much as possible

## Performance Philosophy

If you have a fast computer that can handle heavy computational work in a single frame, the original version might be more suitable for you. 
However, if you have a less powerful computer and prefer a smoother frame rate without stutter, this version might be a better choice.

## Usage

Press `F` (configurable) to toggle the visualization. The mod will scan a 15×10×15 cube around your camera and spawn markers:

1. Green balls — Ground is standable (slope 30–50 degrees)
2. Magenta balls — Ground is steep and non-standable (slope > 50 degrees)
3. No balls — Ground is flat (< 30 degrees)

Flat ground is excluded for performance reasons, as it's obvious enough visually.

## Configuration

Edit `BepInEx/config/Foothold.cfg` (auto-generated on first run) to customize. Or use `ModConfig` to change setting in-game.

1. Ball Color — Different ball colors.
2. Activation key — Change from `F` to any key
3. Activation Mode — Toggle, trigger, or fade away behavior

## Credits

1. @Tzebruh — Created the Foothold? concept and initial implementation
https://github.com/Tzebruh/Foothold
2. All other contributors to the project

## License

This mod maintains the same license as the original Foothold? mod. See LICENSE file for details.