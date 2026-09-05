# -*- coding: utf-8 -*-
"""
RVC worker: фоновый процесс для преобразования голоса в реальном времени.

Запускается из C#-приложения:
    python vc_worker.py --serve        — рабочий режим (TCP-сервер на 127.0.0.1)
    python vc_worker.py --selftest     — проверка установки зависимостей

Протокол (little-endian):
    кадр = [1 байт тип][int32 длина][payload]
    типы: 1 = AudioIn (float32[]), 2 = Control (JSON),
           3 = AudioOut ([float32 volume][float32 process_ms][float32 samples]),
           4 = Event (JSON)

Команды Control: init / update / switch_model / ping / shutdown.
Пути модели (model_path/index_path) принимаются И внутри params,
И на верхнем уровне сообщения — они сливаются в params.
"""

import argparse
import json
import os
import queue
import socket
import struct
import sys
import threading
import time
import traceback

FRAME_AUDIO_IN = 1
FRAME_CONTROL = 2
FRAME_AUDIO_OUT = 3
FRAME_EVENT = 4

HEADER = struct.Struct("<Bi")

# Защита от мусора в сокете: кадры больше этого лимита (или с отрицательной
# длиной) считаются ошибкой протокола. Симметрично лимиту в C# (64 МБ).
MAX_FRAME_BYTES = 64 * 1024 * 1024

# Очередь аудиоблоков между recv-циклом и потоком обработки.
# Если модель не успевает — старые блоки выбрасываются (задержка не растёт).
# 2 блока, не больше: каждый лишний блок в очереди — это +целый блок скрытой задержки.
AUDIO_QUEUE_MAX_BLOCKS = 2


def _bootstrap_sys_path():
    """Гарантирует, что пакет rvc виден: embeddable Python не читает PYTHONPATH."""
    backend = os.environ.get("RVC_BACKEND_DIR") or ""
    if not backend or not os.path.isdir(backend):
        backend = os.path.dirname(os.path.abspath(__file__))
    if os.path.isdir(backend):
        if backend not in sys.path:
            sys.path.insert(0, backend)
        os.environ["RVC_BACKEND_DIR"] = backend
    return backend


BACKEND_DIR = _bootstrap_sys_path()


def log(message, level="info"):
    sys.stderr.write("[%s] %s\n" % (level, message))
    sys.stderr.flush()


# --------------------------------------------------------------------------------------
# Полный обход зависимостей бэкенда
# --------------------------------------------------------------------------------------

ENTRY_MODULES = (
    "rvc.realtime.core",
    "rvc.realtime.worker",
    "rvc.realtime.pipeline",
    "rvc.realtime.client",
    "rvc.realtime.audio",
    "rvc.infer.pipeline",
    "rvc.infer.infer",
    "rvc.lib.utils",
    "rvc.lib.predictors.f0",
    "rvc.configs.config",
)


def scan_missing_modules():
    """Статически обходит import-граф бэкенда и возвращает все недостающие пакеты.

    Так один запуск самопроверки показывает сразу весь список, а не по одному пакету за раз."""
    import ast
    import importlib.util

    root = BACKEND_DIR or os.getcwd()
    stdlib = set(getattr(sys, "stdlib_module_names", ()))
    visited = set()
    missing = {}

    def local_path(module):
        rel = module.replace(".", os.sep)
        for candidate in (rel + ".py", os.path.join(rel, "__init__.py")):
            full = os.path.join(root, candidate)
            if os.path.isfile(full):
                return full
        return None

    def available(top):
        try:
            return importlib.util.find_spec(top) is not None
        except Exception:
            return False

    def handle(name, source):
        top = name.split(".")[0]
        if not top or top in stdlib or top == "__future__":
            return
        if local_path(name) or top == "rvc":
            walk(name)
            return
        if not available(top):
            missing.setdefault(top, os.path.relpath(source, root))

    def walk(module):
        if module in visited:
            return
        visited.add(module)
        path = local_path(module)
        if not path:
            return
        try:
            tree = ast.parse(io_open_text(path))
        except Exception:
            return
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for alias in node.names:
                    handle(alias.name, path)
            elif isinstance(node, ast.ImportFrom):
                if node.level or not node.module:
                    continue
                handle(node.module, path)

    for entry in ENTRY_MODULES:
        if local_path(entry):
            walk(entry)

    return missing


def io_open_text(path):
    with open(path, "rb") as handle:
        return handle.read().decode("utf-8", "replace")


# --------------------------------------------------------------------------------------
# Проверка установки
# --------------------------------------------------------------------------------------

def selftest():
    report = {
        "ok": False,
        "python": sys.version.split()[0],
        "backend": BACKEND_DIR,
        "missing_modules": [],
        "checks": [],
        "warnings": [],
    }

    def check(name, fn):
        try:
            detail = fn()
            report["checks"].append({"name": name, "ok": True, "detail": str(detail)})
            return True
        except Exception as exc:
            report["checks"].append({"name": name, "ok": False, "detail": repr(exc)})
            return False

    results = []

    def torch_check():
        import torch
        info = "torch %s, cuda=%s" % (torch.__version__, torch.cuda.is_available())
        if torch.cuda.is_available():
            info += ", gpu=%s" % torch.cuda.get_device_name(0)
        return info

    results.append(check("torch", torch_check))
    results.append(check("torchaudio", lambda: __import__("torchaudio").__version__))
    results.append(check("numpy", lambda: __import__("numpy").__version__))
    results.append(check("librosa", lambda: __import__("librosa").__version__))
    results.append(check("faiss", lambda: __import__("faiss").__version__ if hasattr(__import__("faiss"), "__version__") else "ok"))
    results.append(check("transformers", lambda: __import__("transformers").__version__))
    results.append(check("pedalboard", lambda: __import__("pedalboard").__version__))
    # Сначала статический обход: собирает ВСЕ недостающие пакеты за один прогон.
    missing = {}

    def deps_check():
        missing.update(scan_missing_modules())
        if missing:
            raise ImportError("нет модулей: " + ", ".join(
                "%s (нужен для %s)" % (name, missing[name]) for name in sorted(missing)))
        return "все зависимости бэкенда на месте"

    results.append(check("зависимости rvc", deps_check))

    # Затем реальные импорты всех модулей, которые использует режим реального времени:
    # если что-то отвалится, в отчёте будут сразу все проблемные строки, а не первая.
    def make_import_check(module, attr=None):
        def run():
            loaded = __import__(module, fromlist=[attr] if attr else [])
            if attr and not hasattr(loaded, attr):
                raise AttributeError("в %s нет %s" % (module, attr))
            return "ok"
        return run

    for module, attr in (
        ("rvc.realtime.core", "VoiceChanger"),
        ("rvc.realtime.pipeline", None),
        ("rvc.realtime.audio", None),
        ("rvc.realtime.utils.vad", None),
        ("rvc.realtime.utils.torch", None),
        ("rvc.infer.pipeline", None),
        ("rvc.lib.utils", None),
        ("rvc.lib.predictors.f0", None),
        ("rvc.configs.config", None),
    ):
        results.append(check(module, make_import_check(module, attr)))

    # Битые нативные пакеты (например, cffi без _cffi_backend.pyd) видны только при
    # реальном импорте. Собираем имена из ModuleNotFoundError в missing_modules,
    # чтобы установщик мог переустановить пакет автоматически.
    import re as _re

    for item in report["checks"]:
        if item["ok"]:
            continue
        for mod_name in _re.findall(r"No module named '([^']+)'", str(item["detail"])):
            top = mod_name.split(".")[0]
            if top and top not in missing:
                missing[top] = item["name"]

    report["missing_modules"] = sorted(missing)

    def predictors_check():
        """Проверяет ВСЕ обязательные веса: rmvpe + contentvec. fcpe — опционален (предупреждение)."""
        backend = BACKEND_DIR or os.environ.get("RVC_BACKEND_DIR", "")
        problems = []

        rmvpe = os.path.join(backend, "rvc", "models", "predictors", "rmvpe.pt")
        if not os.path.isfile(rmvpe):
            problems.append("rmvpe.pt не найден: " + rmvpe)

        contentvec_dir = os.path.join(backend, "rvc", "models", "embedders", "contentvec")
        for name in ("pytorch_model.bin", "config.json"):
            path = os.path.join(contentvec_dir, name)
            if not os.path.isfile(path):
                problems.append("contentvec/%s не найден: %s" % (name, path))

        if problems:
            raise FileNotFoundError("; ".join(problems))

        detail = "rmvpe.pt и contentvec на месте"
        fcpe = os.path.join(backend, "rvc", "models", "predictors", "fcpe.pt")
        if not os.path.isfile(fcpe):
            report["warnings"].append("fcpe.pt отсутствует — метод f0=fcpe будет недоступен")
            detail += "; fcpe.pt отсутствует (опционален)"
        return detail

    results.append(check("predictors", predictors_check))

    report["ok"] = all(results)
    sys.stdout.write(json.dumps(report, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    return 0 if report["ok"] else 2


# --------------------------------------------------------------------------------------
# Обёртка над RVC
# --------------------------------------------------------------------------------------

class VoiceEngine(object):
    """Создаёт и пересоздаёт rvc.realtime.core.VoiceChanger по командам из C#."""

    def __init__(self, send_event):
        self.send_event = send_event
        self.changer = None
        self.params = {}
        self.lock = threading.Lock()

    # ---- вспомогательное ------------------------------------------------------------

    @staticmethod
    def _pedalboard(params):
        """Читает настройки эффектов.

        C# передаёт их вложенным словарём params["pedalboard"] = {...};
        плоские ключи поддерживаются как запасной вариант для совместимости."""
        flat = dict(params)
        nested = params.get("pedalboard")
        if isinstance(nested, dict):
            flat.update(nested)

        return {
            "reverb": bool(flat.get("reverb", False)),
            "reverb_room_size": float(flat.get("reverb_room_size", 0.5)),
            "reverb_wet_level": float(flat.get("reverb_wet_level", 0.33)),
            "reverb_dry_level": float(flat.get("reverb_dry_level", 0.4)),
            "reverb_damping": float(flat.get("reverb_damping", 0.5)),
            "limiter": bool(flat.get("limiter", False)),
            "limiter_threshold": float(flat.get("limiter_threshold", -6.0)),
            "compressor": bool(flat.get("compressor", False)),
            "compressor_threshold": float(flat.get("compressor_threshold", -20.0)),
            "compressor_ratio": float(flat.get("compressor_ratio", 4.0)),
        }

    def _select_device(self, requested):
        import torch

        if requested == "cpu":
            return "cpu"
        if requested == "cuda":
            if torch.cuda.is_available():
                return "cuda:0"
            self.send_event("warn", "CUDA недоступна — переключаемся на CPU")
            return "cpu"

        return "cuda:0" if torch.cuda.is_available() else "cpu"

    # ---- жизненный цикл ------------------------------------------------------------

    def init(self, params):
        with self.lock:
            self.params = dict(params)
            self._build()

    def update(self, params):
        with self.lock:
            rebuild_keys = (
                "read_chunk_size", "cross_fade_overlap_size", "extra_convert_size",
                "f0_method", "embedder_model", "embedder_model_custom",
                "silent_threshold", "vad_enabled", "clean_audio", "clean_strength",
                "post_process", "device", "model_path", "index_path",
            )

            needs_rebuild = any(
                key in params and params[key] != self.params.get(key)
                for key in rebuild_keys
            )

            self.params.update(params)

            if needs_rebuild and self.changer is not None:
                self.send_event("log", "Параметры требуют перезапуска движка — пересоздаём")
                self._build()

    def switch_model(self, params):
        with self.lock:
            self.params.update(params)
            self._build()

    def _build(self):
        from rvc.realtime.core import VoiceChanger

        params = self.params
        device = self._select_device(str(params.get("device", "auto")).lower())

        model_path = params.get("model_path")
        if not model_path or not os.path.isfile(model_path):
            raise FileNotFoundError("Файл модели не найден: %s" % model_path)

        index_path = params.get("index_path") or ""
        if index_path and not os.path.isfile(index_path):
            self.send_event("warn", "Файл .index не найден, работаем без него")
            index_path = ""

        kwargs = dict(
            read_chunk_size=int(params.get("read_chunk_size", 48)),
            cross_fade_overlap_size=float(params.get("cross_fade_overlap_size", 0.05)),
            extra_convert_size=float(params.get("extra_convert_size", 0.50)),
            model_path=model_path,
            index_path=index_path,
            f0_method=params.get("f0_method", "rmvpe"),
            embedder_model=params.get("embedder_model", "contentvec"),
            embedder_model_custom=params.get("embedder_model_custom") or None,
            silent_threshold=float(params.get("silent_threshold", -60)),
            vad_enabled=bool(params.get("vad_enabled", True)),
            clean_audio=bool(params.get("clean_audio", False)),
            clean_strength=float(params.get("clean_strength", 0.5)),
            post_process=bool(params.get("post_process", False)),
            record_audio=False,
            sid=0,
            device=device,
        )

        if kwargs["post_process"]:
            kwargs.update(self._pedalboard(params))

        self.send_event("log", "Загрузка модели на %s: %s" % (device, os.path.basename(model_path)))

        start = time.time()
        try:
            self.changer = VoiceChanger(**kwargs)
        except TypeError:
            # На случай, если версия бэкенда не знает части аргументов.
            kwargs.pop("device", None)
            self.changer = VoiceChanger(**kwargs)

        actual_device = getattr(self.changer, "device", device)
        self.send_event("ready", "Модель готова за %.1f с (устройство: %s)" % (time.time() - start, actual_device))

        # Индекс мог не прочитаться (битый файл, чужая размерность и т.п.).
        # Бэкенд в этом случае работает без индекса — предупреждаем один раз.
        if index_path:
            pipeline = getattr(getattr(self.changer, "vc_model", None), "pipeline", None)
            if pipeline is not None and not getattr(pipeline, "index", None):
                self.send_event(
                    "warn",
                    "Файл .index не удалось загрузить — работаю без него "
                    "(тембр будет менее похож, качество конверсии ниже)",
                )

    # ---- обработка звука -----------------------------------------------------------

    def process(self, audio):
        if self.changer is None:
            return None, 0.0

        params = self.params

        result, volume = self.changer.process_audio(
            audio,
            int(params.get("f0_up_key", 0)),
            float(params.get("index_rate", 0.5)),
            float(params.get("protect", 0.5)),
            float(params.get("volume_envelope", 1.0)),
            bool(params.get("f0_autotune", False)),
            float(params.get("f0_autotune_strength", 1.0)),
            bool(params.get("proposed_pitch", False)),
            float(params.get("proposed_pitch_threshold", 155.0)),
            bool(params.get("use_phase_vocoder", True)),
        )

        return result, volume


# --------------------------------------------------------------------------------------
# TCP-сервер
# --------------------------------------------------------------------------------------

class Server(object):
    def __init__(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.listen(1)
        self.port = self.sock.getsockname()[1]
        self.conn = None
        self.send_lock = threading.Lock()
        self.engine = VoiceEngine(self.send_event)
        self.running = True

        # Аудио обрабатывается в отдельном потоке: recv-цикл никогда не блокируется
        # на модели, а ping/shutdown обрабатываются без задержек даже под нагрузкой.
        self.audio_queue = queue.Queue(maxsize=AUDIO_QUEUE_MAX_BLOCKS)
        self.dropped_blocks = 0
        self.last_drop_warn = 0.0
        self.audio_thread = threading.Thread(target=self._audio_loop, name="audio", daemon=True)

    # ---- отправка --------------------------------------------------------------

    def _send(self, frame_type, payload):
        if self.conn is None:
            return
        try:
            with self.send_lock:
                self.conn.sendall(HEADER.pack(frame_type, len(payload)))
                self.conn.sendall(payload)
        except Exception as exc:
            log("Ошибка отправки: %r" % exc, "error")

    def send_event(self, event, message):
        payload = json.dumps({"event": event, "message": message}, ensure_ascii=False).encode("utf-8")
        self._send(FRAME_EVENT, payload)
        if event != "pong":
            log("%s: %s" % (event, message))

    def send_audio(self, samples, volume, process_ms):
        import numpy as np

        data = np.asarray(samples, dtype=np.float32).tobytes()
        payload = struct.pack("<ff", float(volume), float(process_ms)) + data
        self._send(FRAME_AUDIO_OUT, payload)

    # ---- чтение ----------------------------------------------------------------

    def _recv_exact(self, size):
        if size < 0 or size > MAX_FRAME_BYTES:
            raise ValueError("Некорректная длина кадра: %d" % size)

        chunks = []
        remaining = size

        while remaining > 0:
            chunk = self.conn.recv(remaining)
            if not chunk:
                return None
            chunks.append(chunk)
            remaining -= len(chunk)

        return b"".join(chunks)

    def serve(self):
        # C# ждёт эту строку в stdout, чтобы узнать порт.
        sys.stdout.write("PORT %d\n" % self.port)
        sys.stdout.flush()

        self.conn, _ = self.sock.accept()
        self.conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.send_event("log", "Соединение установлено")

        import numpy as np

        self.audio_thread.start()

        try:
            while self.running:
                header = self._recv_exact(HEADER.size)
                if header is None:
                    break

                frame_type, length = HEADER.unpack(header)

                # Валидация длины до выделения памяти: отрицательный/огромный int32 — ошибка протокола.
                if length < 0 or length > MAX_FRAME_BYTES:
                    self.send_event("error", "Некорректная длина кадра: %d — соединение закрывается" % length)
                    break

                payload = self._recv_exact(length) if length > 0 else b""
                if payload is None:
                    break

                if frame_type == FRAME_CONTROL:
                    self._handle_control(payload)
                elif frame_type == FRAME_AUDIO_IN:
                    self._enqueue_audio(np.frombuffer(payload, dtype=np.float32).copy())
        finally:
            self.running = False
            # Будим поток обработки, чтобы он завершился.
            try:
                self.audio_queue.put_nowait(None)
            except queue.Full:
                pass

        self.send_event("log", "Соединение закрыто")

    # ---- обработчики -----------------------------------------------------------

    def _handle_control(self, payload):
        try:
            message = json.loads(payload.decode("utf-8"))
        except Exception as exc:
            self.send_event("error", "Неверная команда: %r" % exc)
            return

        command = message.get("cmd")
        params = dict(message.get("params") or {})

        # Критично: пути модели могут приходить на верхнем уровне сообщения
        # (так делает C#-мост) — сливаем их в params, иначе _build() их не увидит.
        for key in ("model_path", "index_path"):
            value = message.get(key)
            if value is not None and key not in params:
                params[key] = value

        try:
            if command == "init":
                self.engine.init(params)
            elif command == "update":
                self.engine.update(params)
            elif command == "switch_model":
                self.engine.switch_model(params)
            elif command == "ping":
                self.send_event("pong", "")
            elif command == "shutdown":
                self.running = False
                self.send_event("log", "Остановка по команде")
                try:
                    self.conn.shutdown(socket.SHUT_RDWR)
                except Exception:
                    pass
            else:
                self.send_event("warn", "Неизвестная команда: %s" % command)
        except Exception as exc:
            self.send_event("error", "%s: %s" % (command, exc))
            log(traceback.format_exc(), "error")

    def _enqueue_audio(self, audio):
        """Кладёт блок в очередь. Если очередь полна (модель не успевает) —
        выбрасываем САМЫЙ СТАРЫЙ блок: задержка остаётся ограниченной, а не растёт бесконечно."""
        if self.engine.changer is None:
            return

        try:
            self.audio_queue.put_nowait(audio)
        except queue.Full:
            try:
                self.audio_queue.get_nowait()  # выбрасываем старейший блок
            except queue.Empty:
                pass
            try:
                self.audio_queue.put_nowait(audio)
            except queue.Full:
                pass

            self.dropped_blocks += 1
            now = time.time()
            if now - self.last_drop_warn > 5.0:
                self.last_drop_warn = now
                self.send_event(
                    "warn",
                    "Модель не успевает за реальным временем: выброшено блоков — %d. "
                    "Увеличьте размер чанка или выберите более быстрый режим." % self.dropped_blocks,
                )

    def _audio_loop(self):
        """Поток обработки аудио: берёт блоки из очереди и гонит через модель."""
        while self.running:
            try:
                audio = self.audio_queue.get(timeout=0.5)
            except queue.Empty:
                continue

            if audio is None:
                break

            start = time.time()

            try:
                result, volume = self.engine.process(audio)
            except Exception as exc:
                # Ошибка повторяется на каждом блоке (десятки раз в секунду):
                # печатаем её не чаще раза в 10 секунд, иначе лог заваливается
                # тысячами одинаковых трейсбеков.
                text = str(exc)
                now = time.time()
                self.error_count = getattr(self, "error_count", 0) + 1
                same = text == getattr(self, "last_error_text", None)
                if not same or now - getattr(self, "last_error_at", 0.0) > 10.0:
                    repeats = self.error_count
                    self.last_error_text = text
                    self.last_error_at = now
                    self.error_count = 0
                    suffix = "" if repeats <= 1 else " (повторов: %d)" % repeats
                    self.send_event("error", "Ошибка обработки: %s%s" % (text, suffix))
                    log(traceback.format_exc(), "error")
                continue

            if result is None:
                continue

            self.send_audio(result, volume, (time.time() - start) * 1000.0)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--serve", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()

    backend = BACKEND_DIR or os.environ.get("RVC_BACKEND_DIR")
    if backend and os.path.isdir(backend):
        if backend not in sys.path:
            sys.path.insert(0, backend)
        try:
            os.chdir(backend)
        except OSError:
            pass

    if args.selftest:
        return selftest()

    if args.serve:
        server = Server()
        try:
            server.serve()
        except Exception:
            log(traceback.format_exc(), "error")
            return 1
        return 0

    parser.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main())
