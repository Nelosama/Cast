"""
Cast Backend Service for Windows Desktop Screen Casting.
Handles mDNS discovery of Google Cast / Chromecast devices via pychromecast,
manages media playback on the Cast device, and hosts a persistent HTTP video stream server
and control REST API.
"""

import sys
import time
import socket
import threading
import logging
from collections import deque
from typing import Dict, Any, Optional

from flask import Flask, jsonify, request, Response

try:
    import pychromecast
    PYCHROMECAST_AVAILABLE = True
except ImportError:
    PYCHROMECAST_AVAILABLE = False

logging.basicConfig(level=logging.INFO, format='[%(asctime)s] %(levelname)s: %(message)s')
logger = logging.getLogger("CastBackend")

app = Flask(__name__)

# Global state
discovered_devices: Dict[str, Any] = {}
current_chromecast = None
current_media_controller = None
active_stream_url: Optional[str] = None
active_device_name: Optional[str] = None
is_casting: bool = False
last_error: Optional[str] = None
stop_event = threading.Event()

# Stream Buffer & Clients management
stream_lock = threading.Lock()
stream_clients = []
init_segment = b""  # Stores the fMP4 ftyp + moov initialization header
ring_buffer = deque(maxlen=200)  # Stores recent live stream fragments for fast client startup

def get_local_ip() -> str:
    """Finds the local network IP address of this machine."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
    except Exception:
        ip = "127.0.0.1"
    finally:
        s.close()
    return ip

def is_4k_capable(model_name: str, friendly_name: str) -> bool:
    """Check if device model indicates 4K / Ultra support."""
    name = (model_name + " " + friendly_name).lower()
    keywords = ["ultra", "4k", "google tv 4k", "shield", "bravia 4k"]
    return any(k in name for k in keywords)

def background_discovery_worker():
    """Periodically scans for Chromecast devices on local network in background."""
    global discovered_devices
    while not stop_event.is_set():
        if PYCHROMECAST_AVAILABLE:
            try:
                chromecasts, browser = pychromecast.get_chromecasts()
                new_devices = {}
                for cc in chromecasts:
                    model = cc.model_name or "Chromecast"
                    friendly_name = cc.name or "Unknown Device"
                    new_devices[friendly_name] = {
                        "name": friendly_name,
                        "model_name": model,
                        "uuid": str(cc.uuid),
                        "host": cc.cast_info.host if hasattr(cc, "cast_info") and cc.cast_info else cc.host,
                        "port": cc.cast_info.port if hasattr(cc, "cast_info") and cc.cast_info else cc.port,
                        "is_4k": is_4k_capable(model, friendly_name),
                        "cast_type": getattr(cc, "cast_type", "cast")
                    }
                pychromecast.discovery.stop_discovery(browser)
                with stream_lock:
                    discovered_devices.update(new_devices)
            except Exception as e:
                logger.error(f"Error in background discovery: {e}")
        time.sleep(10)

def monitor_reconnection():
    """Background worker that attempts automatic reconnection if transmission breaks."""
    global current_chromecast, is_casting, last_error
    failed_count = 0
    while not stop_event.is_set():
        time.sleep(4)
        if is_casting and active_device_name and current_chromecast:
            try:
                mc = current_chromecast.media_controller
                state = mc.status.player_state if mc and mc.status else "UNKNOWN"
                if state in ["FAILED"]:
                    failed_count += 1
                    if failed_count <= 3:
                        logger.warning(f"Cast state is {state}. Attempting auto-reconnect ({failed_count}/3)...")
                        reconnect_cast()
                else:
                    failed_count = 0
            except Exception as e:
                logger.error(f"Cast status check failed: {e}")
                last_error = f"Cast state check failed: {e}"
        else:
            failed_count = 0

def reconnect_cast():
    """Reconnects active cast session."""
    global current_chromecast, current_media_controller, is_casting, last_error
    if not PYCHROMECAST_AVAILABLE:
        last_error = "pychromecast library is not installed."
        return False
    if not active_device_name or not active_stream_url:
        return False

    try:
        chromecasts, browser = pychromecast.get_chromecasts()
        matching = [c for c in chromecasts if c.name == active_device_name]
        pychromecast.discovery.stop_discovery(browser)

        if not matching:
            last_error = f"Device '{active_device_name}' not found during reconnect."
            return False

        cc = matching[0]
        cc.wait()
        mc = cc.media_controller
        mc.play_media(active_stream_url, content_type="video/mp4", stream_type="LIVE")
        mc.block_until_active()

        current_chromecast = cc
        current_media_controller = mc
        is_casting = True
        last_error = None
        logger.info(f"Successfully reconnected to {active_device_name}")
        return True
    except Exception as e:
        last_error = f"Reconnection failed: {e}"
        logger.error(last_error)
        return False

# --- TCP Ingest Server for FFmpeg Stream ---
def start_tcp_ingest_server(port=8088):
    """Listens on local TCP socket for FFmpeg stream input and buffers/broadcasts to Flask clients."""
    server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_sock.bind(("0.0.0.0", port))
    server_sock.listen(5)
    logger.info(f"TCP Stream Ingest server listening on 0.0.0.0:{port}")

    def handle_ingest():
        global init_segment
        while not stop_event.is_set():
            try:
                conn, _ = server_sock.accept()
                logger.info("FFmpeg connected to TCP stream ingest server.")

                # Reset buffers for new encoding stream
                with stream_lock:
                    init_segment = b""
                    ring_buffer.clear()

                while not stop_event.is_set():
                    data = conn.recv(32768)
                    if not data:
                        break

                    with stream_lock:
                        # Capture fMP4 initialization header (ftyp + moov box)
                        if len(init_segment) < 65536:
                            init_segment += data

                        ring_buffer.append(data)

                        # Broadcast data chunk to all connected HTTP clients
                        dead_clients = []
                        for q in stream_clients:
                            try:
                                q.append(data)
                            except Exception:
                                dead_clients.append(q)

                        for q in dead_clients:
                            if q in stream_clients:
                                stream_clients.remove(q)

                conn.close()
                logger.info("FFmpeg disconnected from TCP stream ingest server.")
            except Exception as e:
                logger.error(f"Ingest socket error: {e}")
                time.sleep(1)

    t = threading.Thread(target=handle_ingest, daemon=True)
    t.start()

# --- REST API & Streaming Endpoints ---

@app.route("/stream/live.mp4", methods=["GET", "HEAD"])
def stream_video():
    """Serves continuous fragmented MP4 live stream to Chromecast with initialization header delivery."""
    if request.method == "HEAD":
        return Response(status=200, headers={
            "Content-Type": "video/mp4",
            "Accept-Ranges": "none",
            "Access-Control-Allow-Origin": "*"
        })

    def generate():
        client_queue = []
        with stream_lock:
            # Send initial header & buffered chunks to bootstrap player state
            if init_segment:
                client_queue.append(init_segment)
            for chunk in ring_buffer:
                client_queue.append(chunk)

            stream_clients.append(client_queue)

        try:
            while True:
                if client_queue:
                    chunk = client_queue.pop(0)
                    yield chunk
                else:
                    time.sleep(0.01)
        finally:
            with stream_lock:
                if client_queue in stream_clients:
                    stream_clients.remove(client_queue)

    return Response(generate(), mimetype="video/mp4", headers={
        "Access-Control-Allow-Origin": "*",
        "Accept-Ranges": "none"
    })

@app.route("/api/devices", methods=["GET"])
def get_devices():
    """Returns list of discovered Chromecast devices."""
    if not PYCHROMECAST_AVAILABLE:
        return jsonify({
            "status": "warning",
            "message": "pychromecast is not installed on the server",
            "devices": []
        })

    with stream_lock:
        devices_list = list(discovered_devices.values())

    return jsonify({
        "status": "ok",
        "devices": devices_list
    })

@app.route("/api/cast/start", methods=["POST"])
def start_cast():
    """Starts casting a live video stream URL to selected Chromecast device."""
    global current_chromecast, current_media_controller, active_stream_url, active_device_name, is_casting, last_error

    if not PYCHROMECAST_AVAILABLE:
        return jsonify({"status": "error", "message": "pychromecast library is not installed."}), 500

    data = request.json or {}
    device_name = data.get("device_name")
    stream_url = data.get("stream_url")
    content_type = data.get("content_type", "video/mp4")

    if not stream_url:
        stream_url = f"http://{get_local_ip()}:5000/stream/live.mp4"

    if not device_name:
        return jsonify({"status": "error", "message": "device_name is required"}), 400

    try:
        chromecasts, browser = pychromecast.get_chromecasts()
        matching = [c for c in chromecasts if c.name == device_name]
        pychromecast.discovery.stop_discovery(browser)

        if not matching:
            return jsonify({"status": "error", "message": f"Device '{device_name}' not found in network"}), 444

        cc = matching[0]
        cc.wait()
        mc = cc.media_controller
        mc.play_media(stream_url, content_type=content_type, stream_type="LIVE")
        mc.block_until_active()

        current_chromecast = cc
        current_media_controller = mc
        active_stream_url = stream_url
        active_device_name = device_name
        is_casting = True
        last_error = None

        logger.info(f"Started casting to {device_name} stream: {stream_url}")
        return jsonify({
            "status": "ok",
            "message": f"Transmission started to {device_name}",
            "device": device_name,
            "stream_url": stream_url
        })
    except Exception as e:
        last_error = str(e)
        logger.error(f"Failed to start cast: {e}")
        return jsonify({"status": "error", "message": str(e)}), 500

@app.route("/api/cast/stop", methods=["POST"])
def stop_cast():
    """Stops current casting session."""
    global current_chromecast, current_media_controller, active_stream_url, active_device_name, is_casting

    try:
        if current_media_controller:
            current_media_controller.stop()
        if current_chromecast:
            current_chromecast.quit_app()
    except Exception as e:
        logger.error(f"Error stopping cast: {e}")

    is_casting = False
    active_stream_url = None
    active_device_name = None
    current_chromecast = None
    current_media_controller = None

    return jsonify({"status": "ok", "message": "Transmission stopped"})

@app.route("/api/status", methods=["GET"])
def get_status():
    """Returns current status of the backend and cast session."""
    player_state = "IDLE"
    if current_media_controller and current_media_controller.status:
        player_state = current_media_controller.status.player_state or "UNKNOWN"

    local_ip = get_local_ip()

    return jsonify({
        "status": "ok",
        "is_casting": is_casting,
        "active_device": active_device_name,
        "stream_url": active_stream_url,
        "player_state": player_state,
        "local_ip": local_ip,
        "last_error": last_error,
        "device_count": len(discovered_devices),
        "pychromecast_installed": PYCHROMECAST_AVAILABLE
    })

@app.route("/api/bandwidth-check", methods=["GET"])
def bandwidth_check():
    """Performs basic network interface check."""
    local_ip = get_local_ip()
    is_wifi_or_wired = local_ip != "127.0.0.1"

    warning = None
    if not is_wifi_or_wired:
        warning = "No active LAN interface detected. Ensure Wi-Fi or Ethernet is connected."

    return jsonify({
        "status": "ok",
        "local_ip": local_ip,
        "lan_connected": is_wifi_or_wired,
        "estimated_bandwidth_mbps": 100 if is_wifi_or_wired else 0,
        "warning": warning
    })

@app.route("/api/reconnect", methods=["POST"])
def trigger_reconnect():
    """Endpoint to trigger manual reconnect."""
    success = reconnect_cast()
    if success:
        return jsonify({"status": "ok", "message": "Reconnected successfully"})
    else:
        return jsonify({"status": "error", "message": last_error or "Reconnection failed"}), 500

if __name__ == "__main__":
    start_tcp_ingest_server(8088)

    # Start background discovery thread
    discovery_thread = threading.Thread(target=background_discovery_worker, daemon=True)
    discovery_thread.start()

    # Start reconnect monitor thread
    reconnect_thread = threading.Thread(target=monitor_reconnection, daemon=True)
    reconnect_thread.start()

    port = 5000
    host = "0.0.0.0"
    logger.info(f"Starting Cast Backend REST Server on http://{get_local_ip()}:{port}")
    app.run(host=host, port=port, threaded=True)
