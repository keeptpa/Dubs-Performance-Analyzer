# Web Performance Monitor

A local, browser-based dashboard for DPA. It samples the running game on the Unity main
thread and streams the result over a loopback HTTP socket, so you can watch frame times,
stutters, GC pressure and per-method hotspots on a second monitor — or leave it recording
in the background while you play.

This is additive: nothing about the existing in-game window, patches or saved data changed.

---

## Using it

1. Launch RimWorld.
2. **Options → Mod settings → Dubs Performance Analyzer**.
3. Under **Web Performance Monitor**, tick **Enable the local web monitor**.
4. Click **Open in browser**, or go to <http://127.0.0.1:25951/> yourself.

The port is configurable; if it is occupied DPA walks up to the next 20 free ports and the
settings page and page header both show the port that actually bound.

### What each control does

| Control | Effect |
| --- | --- |
| 暂停采样 / Pause | Freezes the sampler. The page keeps showing the last snapshot. |
| 深度分析 / Deep profiling | Turns on DPA's per-method profiling without opening the in-game window. Required for the hotspot and per-mod tables. Costs real frame time — turn it off when you are done. |
| Tick 模式 | Switches DPA between tick-work profiling and per-frame update profiling. Only meaningful while deep profiling is on. |
| 尖峰阈值 | Frame time (ms) at or above which a frame is recorded as a spike. Default 100ms. |
| 清空尖峰 / 重置统计 | Drop recorded spikes, or reset all profiler counters. |

---

## What it shows

**Always on** (cheap, no DPA profiling needed):

- FPS, TPS and target TPS
- Frame time: current, window average, P95, P99, worst
- Per-tick cost and the worst tick in the window, so a spike can be attributed to tick work vs. render work
- Managed heap (MB), process working set (MB)
- GC gen0/1/2 collections per second and in total
- A spike log: every frame over the threshold, with its tick cost, FPS and heap

**While deep profiling is on:**

- Per-method table: average, peak, total, call count and share of the frame
- Per-mod rollup: which mod is eating your TPS, aggregated across methods
- **Per-spike breakdown**: click the method shown in a spike row to see which methods that
  particular frame spent its time on, plus the per-mod split for that frame
- **Spike attribution rollups**: total spike time accumulated per method and per mod across
  the session — the answer to "what keeps hitching" rather than "what hitched once"

> **Why spikes carry no stack trace.** A stack captured at the end of a frame contains only
> `Root.Update` and then this mod's own postfix, because the work has already finished by
> then; it cannot say what the 400ms was spent on. DPA's own stack trace panel works
> differently — you pick one method and it records who *calls* it. The per-spike breakdown
> uses DPA's per-method timers instead, which does answer the question. Nested calls are
> counted more than once, so a breakdown total can exceed the frame time: read it as
> relative share.

---

## How it works

| File | Role |
| --- | --- |
| `Source/Profiling/Web/WebEntry.cs` | Lifecycle, Harmony hooks, and the queue that marshals HTTP requests onto the main thread |
| `Source/Profiling/Web/WebTelemetry.cs` | Main-thread sampler: ring buffers, per-second rollups, spike detection and per-frame attribution |
| `Source/Profiling/Web/WebSnapshot.cs` | Serialises the current state to one JSON document |
| `Source/Profiling/Web/WebServer.cs` | Hand-rolled HTTP/1.1 server on a loopback `TcpListener`, with chunked server-sent events |
| `Source/Profiling/Web/WebProfilerControl.cs` | Starts DPA profiling headlessly (entry discovery, hook installation, entry patching) |
| `Source/Profiling/Web/WebJson.cs` | Minimal JSON writer |
| `Source/Profiling/Web/WebAssets.cs` | Serves the dashboard files out of embedded resources |
| `Source/Profiling/Web/Assets/*` | The dashboard itself — plain HTML/CSS/JS, no CDN, no build step |

Endpoints:

| Route | Purpose |
| --- | --- |
| `GET /` | The dashboard |
| `GET /api/stream` | Server-sent events, pushing a new snapshot at the configured rate |
| `GET /api/snapshot` | One-shot snapshot, used as a fallback if SSE stalls |
| `GET /api/stack?id=N` | The retained call stack for spike N |
| `GET /api/control?action=X&value=Y` | Same actions as the page buttons |

### Design constraints worth knowing

- **Thread affinity.** Every read of `Time.deltaTime`, `Find.TickManager`, `GenTicks` or Harmony
  state happens on the Unity main thread. HTTP threads only ever read a finished JSON string and
  push plain values onto a `ConcurrentQueue` for the main thread to drain.
- **No `HttpListener`.** Under Unity's Mono it is platform dependent, so the server is a raw
  `TcpListener` with a small HTTP parser and explicit chunked encoding. That is what makes SSE
  work reliably.
- **No JSON library.** RimWorld ships none we can rely on, and `JsonUtility` cannot express this
  shape, so `WebJson.cs` writes it directly.
- **DPA will not profile on its own.** Entries are discovered only when `Window_Analyzer` is first
  opened, and the `Root_Play.Update` / `TickManager.DoSingleTick` hooks are installed there too.
  `WebProfilerControl` reproduces that bootstrap so the dashboard can drive DPA without the GUI.
- **Bounded memory.** Frame ring buffers, history and the spike list are all fixed size; a stutter
  storm cannot grow the heap while the game is already struggling.

---

## Building

```powershell
dotnet build "Source\Dubs Performance Analyzer.1.6.csproj" -c Release
```

Output goes to `1.6/Assemblies/PerformanceAnalyzer.dll`. The three dashboard assets are embedded
into the assembly, so there is nothing extra to ship.
