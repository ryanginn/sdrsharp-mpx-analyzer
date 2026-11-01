# MPX Analyzer for SDR#

An [SDR#](https://airspy.com/download/) plugin for broadcast FM that:

- **Shows the MPX (composite) spectrum** from 0 to 96 kHz in its own window, so you can see the 19 kHz stereo pilot, the 38 kHz L−R subcarrier, 57 kHz RDS and any SCA subcarriers.
- **Measures** MPX power (ITU-R BS.412), peak deviation, pilot and RDS injection levels, and whether RDS is phase-locked to the pilot.
- **Records the raw MPX signal** as a mono, 192 kHz WAV file. This is the demodulated composite signal, not de-multiplexed audio.

This is a port of the SDR++ MPX Analyzer plugin in this repository. The DSP and the measurements are the same; the parts that depend on the host application differ (see [Differences from the SDR++ version](#differences-from-the-sdr-version)).

## How it works

The plugin taps SDR#'s **decimated and filtered IQ**: the Radio's own channel, after SDR#'s IF filter. It therefore always follows the frequency and the filter bandwidth you set in SDR#'s main window. It FM-demodulates that IQ itself, so the level scaling doesn't depend on SDR#'s audio or volume settings.

The demodulated signal is resampled from SDR#'s IQ rate (typically 250–500 kS/s in WFM mode, depending on the source sample rate) to 192 kHz. The resampling filter also corrects the demodulator's high-frequency roll-off. The demodulator measures the phase change over one IQ sample, which at 312.5 kS/s would read 0.6% low at 19 kHz and 5.4% low at 57 kHz. After the correction the MPX is flat up to about 86 kHz. That 192 kHz signal feeds:

- the spectrum display (4096-point FFT, Blackman-Harris window, 50% overlap)
- the measurements
- the WAV recorder

**Filter bandwidth:** SDR#'s WFM filter narrower than about 200 kHz cuts into the FM sidebands of the higher subcarriers. That raises noise and distortion around 38–60 kHz and changes the readings. For MPX work, set the filter bandwidth to about 200–250 kHz on a station without strong neighbours. Keep the IQ rate at 192 kS/s or more, which SDR# does automatically once the bandwidth is wide enough. The plugin panel shows the IF bandwidth and IQ rate, and warns when either is too low or when the demodulator isn't set to WFM.

**Levels:** the MPX is scaled so that ±1.0 = ±75 kHz deviation. On the spectrum, **0 dB is a sine at 75 kHz deviation**. A 9% (6.75 kHz) pilot therefore reads about −21 dB, and RDS at 2–4 kHz injection reads roughly −30 to −40 dB, spread over its ~5 kHz bandwidth.

**Measurements** (shown to the right of the spectrum):
- **MPX power (dBr):** the power of the complete MPX signal (audio, pilot, RDS and everything else), as defined in ITU-R BS.412.
  - **Reference:** 0 dBr is the power of a sine tone giving ±19 kHz deviation.
  - **Main reading:** the RMS over a sliding 60-second window, as BS.412 specifies. For the first minute after tuning it shows the average so far, with the time covered (e.g. "MPX power (12 s)").
  - **Short-term reading:** a 1-second value is shown underneath for spotting changes quickly.
  - **Highlight:** the reading turns amber above 0 dBr, the most common limit. Some regulators allow +3 or +6 dBr.
- **Deviation (kHz):** the peak frequency deviation over the last second, plus a max hold. Both turn red above ±75 kHz.
  - **What "peak" means here:** this is the true sample peak of the demodulated signal, so noise on weak signals pushes it up. Professional modulation meters may apply extra filtering or a peak-counting rule, so expect small differences from them.
- **Pilot (kHz):** the level of the 19 kHz stereo pilot as deviation, averaged over 1 second. A typical 9% pilot reads 6.75 kHz; hover over it to see it as a percentage.
  - **How:** the MPX is correlated with a 19 kHz reference over 10 ms windows, so stereo audio and noise hardly affect it.
  - **No pilot:** shows "none" below 0.5 kHz, e.g. on mono stations.
- **RDS (kHz):** the RDS injection level as peak deviation, averaged over 1 second. Typical values are 2–4 kHz; hover over it to see it as a percentage.
  - **How:** the 57 kHz band is shifted to 0 Hz and filtered to the RDS bandwidth. RDS has almost no energy exactly at 57 kHz, while noise does, so the noise in the band is estimated and removed. The remaining RMS level is converted to peak deviation with a crest factor of 1.51. That's the value real encoders show against reference instruments. The theoretical EN 50067 waveform would give 1.444, about 4.5% lower.
  - **No RDS:** shows "none" when the band holds less than twice as much RDS power as noise, or the level is below 0.3 kHz.
- **RDS lock:** whether the RDS 57 kHz subcarrier is phase-locked to the third harmonic of the 19 kHz pilot, as EN 50067 / IEC 62106 require for stereo stations. The standard allows in phase (0°) or quadrature (90°), within ±10°.
  - **Readings:** *Yes* with the phase offset (e.g. "Yes +2°"), green when within tolerance and amber when not. *No* (amber) means the RDS encoder isn't locked to the pilot. *n/a* means there is no pilot or no RDS.
  - **How:** squaring the RDS signal removes its data and leaves the carrier phase. This is compared with 3× the pilot phase every 10 ms and averaged over 5 seconds. Hover for the exact phase and the stability figure (locked above 0.90).
  - **180° ambiguity:** RDS's data modulation hides 180° phase steps, so the offset is shown in the range −90° to +90°.
- **CPU use:** the spectrum and measurements are only computed while the MPX window is open. Recording carries on when it's closed. Reopening the window restarts the measurements.
- **Reset:** the measurements restart automatically when the frequency, the filter bandwidth or the IQ rate changes. *Reset* under the meters restarts them by hand.
- **Accuracy:** a slow DC filter removes any carrier offset first, so slight mistuning doesn't affect the readings. SDR#'s IF filter does affect them; use a wide bandwidth on a clean signal for measurements.

**WAV files:**
- **Sample format:** Int16 (default) is enough for most uses and keeps files small. Int32 gives more dynamic range. Both clip at ±75 kHz deviation; choose Float32 if you need to capture over-deviating stations without clipping.
- **Bandwidth:** 192 kHz sampling gives a 96 kHz Nyquist limit. Content up to about 86 kHz is flat.
- **File size:** recordings are split into a new file before reaching the 4 GiB WAV limit, which is about 3 hours at Int16 (about 93 minutes at Int32/Float32). Int16 uses 384 kB/s, about 1.4 GB per hour. The WAV header is updated every 10 seconds, so a recording stays readable if SDR# stops unexpectedly.
- **File names:** `mpx_<frequency>Hz_<YYYYMMDD>_<HHMMSS>.wav`, in the folder you choose (default `<SDR# folder>\Recordings`, created when you first record).

## Requirements

- A current **SDR#** (.NET build, from about 2020 onwards; the pre-built plugin is built against the release that was current at build time).
- A source sample rate high enough for a **200–250 kHz** WFM filter (any Airspy, RTL-SDR at ≥ 1 MS/s, etc.).

## Installing

**Pre-built (GitHub Actions):** every push builds `SDRSharp.MPXAnalyzer.dll` against the SDR# release current at that time. Download it from the run's *Artifacts* (`mpx_analyzer_sdrsharp`), or from a GitHub Release. `SDRSHARP_VERSION.txt` shows which SDR# version and .NET version it was built for.

1. Close SDR#.
2. Copy `SDRSharp.MPXAnalyzer.dll` into SDR#'s `Plugins` folder. Windows may block downloaded DLLs: if the plugin doesn't appear, right-click the DLL → *Properties* → tick *Unblock*.
3. Start SDR#. **MPX Analyzer** appears in the plugin list (left-hand menu, *Analysis* category). Older SDR# versions that use `Plugins.xml` instead need this line inside its `<sharpPlugins>` section:
   ```xml
   <add key="MPX Analyzer" value="SDRSharp.MPXAnalyzer.MpxPlugin,SDRSharp.MPXAnalyzer" />
   ```

If SDR# is updated to a new .NET version, rebuild the plugin (or download a newer build).

## Usage

1. Tune SDR# to an FM broadcast station in **WFM** mode, and set the filter bandwidth to about 200–250 kHz.
2. Open the **MPX Analyzer** panel and press **Show MPX window**. The window remembers its position, and reopens with SDR# if it was open when SDR# closed.
3. Hover over the spectrum to read frequency and level. Enable **Peak hold** to catch transients.
4. Choose a folder and sample type in the panel, then press **Record MPX**. You can also use the **red record button** at the top right of the MPX window; it turns into a square stop button, with the elapsed time beside it, while recording.

### Panel options

| Option | Description |
|---|---|
| Show MPX window | Opens or hides the spectrum and measurements window |
| Averaging | Number of FFT frames averaged (about 94 frames/s) |
| Max freq | Upper edge of the display (20–96 kHz) |
| dB max / dB min | Vertical scale |
| Peak hold | Overlay the maximum level seen since the last reset |
| Level units | Show deviation, pilot and RDS levels in kHz (default) or percent, where 100% = 75 kHz. Hover over Pilot/RDS to see the other unit |
| Folder | Where recordings are saved |
| Sample type | Int16 (default) / Int32 / Float32 WAV samples |

Settings are saved in `MPXAnalyzer.json` in SDR#'s folder (or in `%LOCALAPPDATA%\SDRSharp.MPXAnalyzer` if that folder isn't writable).

## Differences from the SDR++ version

- **No separate VFO:** SDR# has no API for extra VFOs, so the plugin analyses the Radio's own channel. This is the SDR++ version's default (*Follow VFO: Radio*). The IF bandwidth is SDR#'s filter bandwidth.
- **No VFO ripple correction:** the SDR++ version measures and cancels the passband ripple of SDR++'s channel resampler. SDR#'s decimation and IF filter are part of SDR# and can't be reproduced by the plugin, so they are not corrected. Use a wide filter bandwidth for measurements.
- **No audio output:** SDR# has no way for plugins to add audio streams. Use the WAV recording instead.

## Building

Requires the .NET SDK matching SDR#'s .NET version (see `SDRSharp.runtimeconfig.json` in SDR#'s folder, e.g. `"tfm": "net9.0"`), on Windows.

```bat
cd sdrsharp
dotnet build -c Release -p:SdrSharpDir="C:\path\to\sdrsharp" -p:TargetFramework=net9.0-windows
```

`SdrSharpDir` is the folder with `SDRSharp.Common.dll` and `SDRSharp.Radio.dll` (default `sdrsharp\sdrsharp-bin`). Copy `bin\Release\<framework>\SDRSharp.MPXAnalyzer.dll` into SDR#'s `Plugins` folder.

### Tests

The DSP code has no SDR# dependency and is tested with synthetic FM signals of exactly known levels, fed in as IQ at several IQ rates, as SDR# would. The FM is generated with continuous phase, like a real transmitter. Runs on any OS:

```sh
dotnet run -c Release --project sdrsharp/tests/MpxTests.csproj
```

It checks the pilot, RDS (with and without noise), MPX power, deviation, flatness at 57 kHz, RDS lock detection and the WAV writer. CI also runs `smoketest/`, which loads the plugin with SDR#'s own assemblies on Windows, feeds it a signal through the IQ hook, checks the readings and saves screenshots (artifact `sdrsharp_smoketest_screenshots`).

## License

GPL-3.0, the same as the SDR++ version.
