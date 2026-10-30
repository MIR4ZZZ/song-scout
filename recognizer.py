import argparse
import asyncio
import io
import json
import sys
import wave

import numpy as np
import soundcard as sc
from aiohttp_retry import ExponentialRetry
from shazamio import HTTPClient, SearchParams, Shazam


CAPTURE_SECONDS = 8
SAMPLE_RATE = 48_000


def pcm_to_wav(audio: np.ndarray, sample_rate: int = SAMPLE_RATE) -> bytes:
    if audio.ndim == 2:
        audio = audio.mean(axis=1)
    audio = np.nan_to_num(audio, nan=0.0, posinf=1.0, neginf=-1.0)
    pcm = (np.clip(audio, -1.0, 1.0) * 32767).astype("<i2")
    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm.tobytes())
    return output.getvalue()


def capture_desktop_audio(seconds: int = CAPTURE_SECONDS) -> bytes:
    speaker = sc.default_speaker()
    if speaker is None:
        raise RuntimeError("Windows has no default output device.")
    loopback = sc.get_microphone(speaker.id, include_loopback=True)
    if loopback is None:
        raise RuntimeError("The default output device has no loopback capture endpoint.")

    with loopback.recorder(samplerate=SAMPLE_RATE) as recorder:
        audio = recorder.record(numframes=seconds * SAMPLE_RATE)

    if audio.size == 0 or float(np.max(np.abs(audio))) < 0.0001:
        raise RuntimeError("No desktop audio was detected. Play the song and try again.")
    return pcm_to_wav(audio)


def parse_result(response: dict) -> dict:
    matches = response.get("matches") or []
    track = response.get("track") or {}
    if not matches or not track:
        return {"ok": False, "status": "No song matched. Try a louder or clearer section."}

    images = track.get("images") or {}
    return {
        "ok": True,
        "status": "Recognized",
        "title": track.get("title") or "Unknown title",
        "artist": track.get("subtitle") or "Unknown artist",
        "url": track.get("url") or track.get("share", {}).get("href") or "",
        "cover": images.get("coverarthq") or images.get("coverart") or "",
    }


async def recognize(wav_bytes: bytes) -> dict:
    retries = ExponentialRetry(
        attempts=4,
        max_timeout=8,
        statuses={429, 500, 502, 503, 504},
    )
    client = HTTPClient(retry_options=retries)
    try:
        async with Shazam(http_client=client) as shazam:
            response = await shazam.recognize(
                wav_bytes,
                options=SearchParams(segment_duration_seconds=CAPTURE_SECONDS),
            )
    finally:
        await client.close()
    return parse_result(response)


def self_test() -> None:
    time = np.arange(SAMPLE_RATE, dtype=np.float32) / SAMPLE_RATE
    wav_bytes = pcm_to_wav(np.sin(2 * np.pi * 440 * time).astype(np.float32))
    assert wav_bytes[:4] == b"RIFF"
    matched = parse_result(
        {
            "matches": [{"id": "test"}],
            "track": {"title": "Test Song", "subtitle": "Test Artist", "url": "https://example.com"},
        }
    )
    assert matched["ok"] and matched["title"] == "Test Song"
    assert not parse_result({"matches": []})["ok"]
    print(json.dumps({"ok": True, "status": "Self-test passed"}))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0

    try:
        wav_bytes = capture_desktop_audio()
        result = asyncio.run(recognize(wav_bytes))
    except Exception as error:
        result = {"ok": False, "status": str(error) or type(error).__name__}

    print(json.dumps(result, ensure_ascii=False), flush=True)
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
