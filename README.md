# CastDesktop HD 🖥️ 📡 📺

Aplicación de escritorio para Windows desarrollada en C# (.NET) y Python que permite capturar la pantalla (o ventana específica) del usuario y transmitirla a un dispositivo Google Cast / Chromecast en la misma red local, priorizando **ALTA CALIDAD DE IMAGEN** y nitidez de texto sobre latencia mínima.

---

## 🌟 Características Principales

1. **Descubrimiento Automático mDNS / DIAL:**
   - Detecta automáticamente dispositivos Chromecast, Chromecast Ultra y Google TV en la red local utilizando `pychromecast` y `zeroconf`.
2. **Captura de Pantalla Completa o Ventana Especifica:**
   - Permite elegir la pantalla completa del escritorio o una ventana por su título.
3. **Codificación orientada a Alta Calidad de Imagen:**
   - Ajustes de FFmpeg H.264 (Perfil High) y H.265 (HEVC).
   - Resoluciones nativas (1080p y hasta 4K en Chromecast Ultra / Google TV).
   - Bitrates configurables de 15 a 50 Mbps y framerate de 30 a 60 FPS.
   - Presets de codificación `slow` / `medium` para mantener nitidez en lecturas de texto e imágenes fijas.
   - Latencia configurada de 1 a 3 segundos (prioriza buffer y estabilidad sobre velocidad inmediata).
4. **Selector de Calidad Flexible:**
   - **Alta:** 1080p/4K, 60 FPS, 35 Mbps (H.264 High Profile, Preset Slow).
   - **Media:** 1080p, 30 FPS, 18 Mbps (H.264 High Profile, Preset Medium).
   - **Baja:** 720p, 30 FPS, 6 Mbps (H.264 Main Profile).
   - **Personalizada:** Ajuste manual de FPS, Bitrate y Códec.
5. **Telemetría y Monitor en Tiempo Real:**
   - Panel de control con FPS actual, Bitrate actual, Latencia estimada y estado de conexión.
   - Advertencia automática de ancho de banda e interfaz de red.
   - Botón de reconexión manual y reconexión automática en background si la transmisión se interrumpe.

---

## 🏗️ Arquitectura del Sistema

```
+------------------------------------+           +----------------------------------+
|      CastDesktop (C# WPF UI)       |           |     Python Backend (app.py)     |
|  - Selección de pantalla/ventana   |  HTTP API |  - Descubrimiento mDNS / Cast    |
|  - Selector de calidad             | --------> |  - Control pychromecast          |
|  - Telemetría & Logs               |           |  - Reconexión automática         |
+------------------------------------+           +----------------------------------+
                 |                                                 |
                 v                                                 v
  [Proceso FFmpeg (libx264/libx265)]                   [Google Cast Device / Chromecast]
     Servidor HTTP local (live.ts) <----------------------- Reproduce URL de stream
```

---

## ⚙️ Requisitos del Sistema y Dependencias

### Requisitos Software
- **Sistema Operativo:** Windows 10 / Windows 11 (64-bit).
- **Runtime .NET:** .NET 8.0 SDK / .NET 10.0 SDK.
- **Python:** Python 3.9 o superior.
- **FFmpeg:** `ffmpeg` instalado y agregado a las variables de entorno (`PATH`) o ubicado en la misma carpeta que el ejecutable.

### Instalar Dependencias de Python

```bash
pip install -r src/cast_backend/requirements.txt
```

Las librerías requeridas en `src/cast_backend/requirements.txt` son:
- `pychromecast>=14.0.0`
- `zeroconf>=0.130.0`
- `flask>=3.0.0`
- `requests>=2.31.0`
- `psutil>=5.9.0`

---

## 🚀 Instrucciones de Ejecución

### 1. Iniciar el Servidor Backend de Python
Desde la carpeta raíz del proyecto, ejecuta:

```bash
python src/cast_backend/app.py
```
*El backend escuchará en `http://0.0.0.0:5000` y comenzará el escaneo mDNS.*

### 2. Compilar y Ejecutar la Aplicación C# WPF

Desde la carpeta raíz del proyecto:

```bash
dotnet build src/CastDesktop/CastDesktop.csproj /p:EnableWindowsTargeting=true
dotnet run --project src/CastDesktop/CastDesktop.csproj /p:EnableWindowsTargeting=true
```

---

## 🖥️ Guía de Uso

1. **Abrir la aplicación CastDesktop HD.**
2. **Seleccionar Fuente:** Selecciona "Pantalla Completa" o "Ventana Específica" e ingresa el título de la ventana.
3. **Dispositivo Chromecast:** Haz clic en `🔄 Buscar` si el dispositivo no aparece de inmediato y selecciónalo en el desplegable.
4. **Selector de Calidad:**
   - Selecciona **Alta** para presentaciones de texto, desarrollo o visualización nítida en monitores HD/4K.
   - Selecciona **Media** o **Baja** si la red Wi-Fi presenta inestabilidad o fluctuaciones.
5. **Iniciar Transmisión:** Pulsa `▶ INICIAR TRANSMISIÓN`. El botón cambiará a rojo `⏹ DETENER TRANSMISIÓN`.
6. **Reconexión:** Si el Chromecast pierde brevemente la red, la aplicación intentará reconectarse automáticamente o puedes presionar `🔄 Reconectar Dispositivo`.

---

## 🛠️ Manejo de Errores e Incidencias Comunes

- **No se encuentran dispositivos Chromecast:**
  - Asegúrate de que el PC con Windows y el Chromecast estén en la misma subred de la red local.
  - Comprueba que la red Wi-Fi no tenga activado el "Aislamiento de AP" (AP Isolation).
- **FFmpeg no encontrado:**
  - Descarga FFmpeg desde [ffmpeg.org](https://ffmpeg.org/download.html) y coloca `ffmpeg.exe` en la carpeta binaria del proyecto o añádelo a `PATH`.
- **Advertencia de Ancho de Banda Insuficiente:**
  - Si la transmisión sufre caídas de frames, cambia el perfil de calidad a **Media** o **Baja** para reducir el bitrate de 35 Mbps a 18/6 Mbps.
