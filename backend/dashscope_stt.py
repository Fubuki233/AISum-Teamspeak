#!/usr/bin/env python3
import json
import os
import sys
import time

from dashscope.audio.asr import Recognition, RecognitionCallback


class _Callback(RecognitionCallback):
    pass


def main() -> int:
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "wav path required"}, ensure_ascii=False))
        return 2

    wav_path = sys.argv[1]
    sample_rate = int(sys.argv[2]) if len(sys.argv) > 2 else 48000
    api_key = os.getenv("DASHSCOPE_API_KEY", "").strip()
    requested_model = os.getenv("DASHSCOPE_STT_MODEL", "fun-asr-realtime-2026-02-28").strip() or "fun-asr-realtime-2026-02-28"

    if not api_key:
        print(json.dumps({"ok": False, "error": "missing DASHSCOPE_API_KEY"}, ensure_ascii=False))
        return 3

    # For live local WAV/raw audio from the TS client, use the verified realtime model.
    # If an old generic name is provided, normalize it to the concrete supported model.
    model = (
        "fun-asr-realtime-2026-02-28"
        if requested_model in {"", "fun-asr"}
        else requested_model
    )

    try:
        last_resp = None
        last_error = None

        for attempt in range(3):
            try:
                recog = Recognition(
                    model=model,
                    callback=_Callback(),
                    format="wav",
                    sample_rate=sample_rate,
                )
                resp = recog.call(wav_path)
                last_resp = resp

                if getattr(resp, "status_code", None) == 200:
                    sentences = []
                    output = getattr(resp, "output", None) or {}
                    if isinstance(output, dict):
                        sentences = output.get("sentence") or []

                    text = "".join((s.get("text") or "") for s in sentences).strip()
                    print(json.dumps({
                        "ok": True,
                        "text": text,
                        "model": model,
                        "requested_model": requested_model,
                    }, ensure_ascii=False))
                    return 0

                code = getattr(resp, "code", "") or ""
                message = getattr(resp, "message", "") or ""
                transient = (
                    getattr(resp, "status_code", None) == -1
                    or "Temporary failure in name resolution" in message
                    or "ClientConnectorError" in code
                )
                if attempt < 2 and transient:
                    time.sleep(1.5 * (attempt + 1))
                    continue

                print(json.dumps({
                    "ok": False,
                    "status_code": getattr(resp, "status_code", None),
                    "code": code,
                    "message": message,
                    "model": model,
                    "requested_model": requested_model,
                }, ensure_ascii=False))
                return 4
            except Exception as ex:
                last_error = ex
                if attempt < 2:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                raise

        if last_resp is not None:
            print(json.dumps({
                "ok": False,
                "status_code": getattr(last_resp, "status_code", None),
                "code": getattr(last_resp, "code", ""),
                "message": getattr(last_resp, "message", ""),
                "model": model,
                "requested_model": requested_model,
            }, ensure_ascii=False))
            return 4

        raise last_error if last_error is not None else RuntimeError("unknown DashScope failure")
    except Exception as ex:
        print(json.dumps({"ok": False, "error": str(ex), "model": model}, ensure_ascii=False))
        return 5


if __name__ == "__main__":
    raise SystemExit(main())
