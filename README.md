# AudioLinkKeeper

Stops your Bluetooth speaker from silencing the **entire** Windows system when you pause a video.

[中文说明](#中文说明)

---

## The bug

You have a Bluetooth speaker. You pause a video in your browser. Now **nothing on the
computer makes a sound** — not the game you were playing, not the system beep when you
drag the volume slider, nothing.

Playing media again in any browser or music player brings it back. Reconnecting Bluetooth
also works. It looks random. It is not.

## What is actually happening

Bluetooth audio runs two independent channels:

| Channel | Carries |
| --- | --- |
| **A2DP** | the audio data |
| **AVRCP** | playback control and volume |

When a media app that registers **System Media Transport Controls** (SMTC) pauses,
Windows propagates that paused state over AVRCP. Some speakers respond by closing their
audio channel. **The A2DP driver is never told**, so it keeps streaming at full rate into
a channel that is no longer being consumed.

The result is a failure that is invisible from inside Windows:

- audio endpoint: active, unmuted, correct format
- session peak meters: moving
- `DevicePosition`: advancing at a steady 44,100 frames/sec
- `Microsoft-Windows-Audio/GlitchDetection`: completely empty

Failed-state and healthy-state readings are byte-for-byte identical. Meanwhile the speaker
plays nothing.

The giveaway is that **AVRCP volume sync breaks at the same moment**: the volume buttons on
the speaker stop moving the Windows volume slider, and vice versa. Two independent channels
failing together points at the layer beneath both.

## Why the usual fixes do not work

Keep-alive tools that push a WASAPI stream (the standard remedy for *device idle sleep*)
have no effect here, because this is not idle sleep. Verified against the failed state:

| Attempt | Result |
| --- | --- |
| Raw WASAPI playback — polling and event-driven, with/without `AUDIO_STREAM_CATEGORY`, new stream vs reused stream, `Start`/`Stop` cycles, silence vs waveform, amplitude up to 0.41, parameters copied verbatim from Chromium | ❌ |
| `System.Media.SoundPlayer` (high-level `PlaySound`) | ❌ |
| Rebuilding the audio endpoint | ❌ |
| Power requests (display / system / execution) | ❌ |
| Disabling adapter power saving, USB selective suspend | ❌ |
| Disabling unused audio endpoints | ❌ |
| Restarting the A2DP device node | ✅ but takes ~8 s and cuts audio |
| **Playback from any app that registers SMTC — even pure silence** | ✅ instant |

The audio content is irrelevant. The SMTC registration is the whole story.

## The fix

Keep one permanently-playing, zero-volume SMTC session alive. No pause is ever propagated,
so the speaker never closes its channel.

```bash
AudioLinkKeeper.exe          # run the keeper
AudioLinkKeeper.exe once     # one silent burst, for manual rescue
```

Put a shortcut in `shell:startup` to have it run at login. It shows up in the Win+A control
centre as a track named `AudioLinkKeeper` that plays forever and makes no sound.

### Implementation notes

Three things that are not obvious and cost real time to find:

1. **Do not set every `CommandManager` behavior to `Never`.** Windows decides the session
   has no usable controls, hides it from SMTC entirely, and the fix silently stops working.
   Disable only next/previous.
2. **Event-driven recovery defeats itself.** "Detect a pause, then play a silent burst"
   fails: when your burst finishes, *your own* session goes to stopped and re-triggers the
   bug. It has to be permanently playing.
3. **`PlaybackState.None` means the `MediaPlayer` is dead.** `Play()` on it is a silent
   no-op. A naive watchdog will happily log "recovering" once a second forever while doing
   nothing — the keeper must dispose and rebuild the player.

Also: display metadata has to go through `MediaPlaybackItem.ApplyDisplayProperties`, or the
control centre shows the raw wav path as the track title.

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 10 runtime

## Build

```bash
dotnet publish src/AudioLinkKeeper.csproj -c Release
```

`silent.wav` is generated at runtime next to the executable and regenerated if deleted.
`keeper.log` rotates at 512 KB and keeps one previous file, so disk usage is capped at
about 1 MB.

## Caveats

- Verified on a Harman Kardon SoundSticks 4 over A2DP with an Intel adapter on Windows 11.
  Whether other speakers close their channel the same way is unknown.
- The AVRCP forwarding step is **inferred**, not packet-captured. What is directly measured
  is that SMTC-registered playback recovers audio and non-SMTC playback never does, and that
  AVRCP volume sync fails at the same time as the audio.

## License

MIT

---

# 中文说明

## 症状

蓝牙音箱，在浏览器里暂停一个视频，然后**整台电脑都没声音了**——游戏、系统提示音、拖动音量条那声「噔」，全都没了。

去任意浏览器或音乐播放器随便播点什么，声音就回来了。重连蓝牙也行。看着像随机故障，其实不是。

## 真正的原因

蓝牙音频跑着两条独立通道：**A2DP** 运音频数据，**AVRCP** 传播放控制和音量。

当一个注册了**系统媒体传送控件（SMTC）**的应用暂停时，Windows 会把「已暂停」经 AVRCP 通知音箱，某些音箱据此关闭自己的音频通道。**而 A2DP 驱动完全不知道这件事**，继续以每秒 44100 帧的速度往一个已经没人接收的通道里灌数据。

于是故障在 Windows 内部完全不可见：端点正常、电平在跳、设备位置匀速推进、系统自带的音频故障检测器零告警——哑火状态和正常状态的读数逐字节一致。

关键线索是：**哑火的同时，AVRCP 音量联动也失效了**（按音箱音量键 Windows 不动，反之亦然）。两条独立通道同时死，问题只能在它们共同的下层。

## 为什么常见办法没用

针对「设备空闲休眠」的保活工具（往 WASAPI 推流那种）在这里**完全无效**，因为这压根不是休眠。上面英文表格里列了实测过的所有手段：八种 WASAPI 姿势、高层播放 API、重建端点、电源请求、关省电、禁用多余端点——全部失败；**唯独注册了 SMTC 的应用播放能恢复，哪怕播的是纯静音**。

音频内容根本不重要，重要的是有没有注册 SMTC。

## 解法

常驻一个永远在播放、音量为 0 的 SMTC 会话。这样暂停状态永远不会被传播出去，音箱也就永远不会关闭通道。

放个快捷方式到 `shell:startup` 即可开机自启。它会出现在 Win+A 控制中心里，是一个永远在播放、但永远没有声音的条目。

三个不容易想到的坑，都写在上面英文的 Implementation notes 里：**不能把所有命令都设成 Never**（会话会被系统隐藏，当场失效）、**不能用「检测到哑火再补播」的思路**（补播结束时自己会再触发一次 bug）、**`PlaybackState.None` 时必须重建播放器**（此时 `Play()` 是无声的空操作，看门狗会一边刷日志一边什么都没做）。

## 免责

在 Harman Kardon SoundSticks 4 + Intel 蓝牙 + Windows 11 上验证。其他音箱是否是同样的行为未知。AVRCP 转发那一步是**推断**，没有抓包证实——直接测到的事实是：注册 SMTC 的播放能恢复、不注册的一律不行，且音量联动与音频同时失效。
