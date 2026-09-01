# CastDesktop HD 🖥️ 📡 📺

Aplicación de escritorio para Windows desarrollada en C# (.NET 8 WPF) que permite capturar la pantalla (o ventana específica) del usuario y transmitirla directamente a un dispositivo Google Cast / Chromecast en la misma red local, priorizando **ALTA CALIDAD DE IMAGEN** y nitidez de texto sobre latencia mínima.

---

## 🌟 Características Principales

1. **Aplicación Nativa de Instalación Única (Sin Python, Pip ni PATH):**
   - Todo el motor de descubrimiento mDNS, servidor de streaming de video HTTP y control de reproducción Chromecast está escrito 100% en C# (.NET 8).
   - Se instala mediante un único archivo instalador `CastDesktop_Setup.exe`.
2. **Descubrimiento Automático mDNS (Zeroconf + Sharpcaster):**
   - Detecta automáticamente dispositivos Chromecast, Chromecast Ultra y Google TV en la red local en segundo plano.
3. **Captura de Pantalla Completa o Ventana Específica:**
   - Permite transmitir el escritorio completo o seleccionar una ventana por su título.
4. **Codificación Orientada a Alta Calidad de Imagen:**
   - Soporte H.264 (High Profile) y H.265 (HEVC).
   - Resoluciones nativas (1080p y hasta 4K en Chromecast Ultra / Google TV).
   - Bitrates configurables (15 a 50 Mbps) y framerates de 30 a 60 FPS.
   - Presets de codificación orientados a nitidez de texto (`slow` / `medium`).
5. **Diagnóstico de Red Real y Permisos:**
   - Verificación de la velocidad real de enlace de la tarjeta de red (Ethernet/Wi-Fi) con advertencias automáticas si el bitrate configurado excede la capacidad del enlace.
   - Detección específica de errores de permisos de captura de pantalla de Windows con diálogos explicativos.
6. **Telemetría y Monitor en Tiempo Real:**
   - Muestreo constante de FPS, Bitrate actual, latencia estimada y estado del buffer.

---

## 📦 Instalación

Ya no hace falta compilar nada manualmente. El instalador ejecutable siempre está disponible y actualizado automáticamente tras cada cambio:

1. Ve a la pestaña **Releases** de este repositorio en GitHub y descarga la última versión de `CastDesktop_Setup.exe`.
2. Ejecuta el instalador y sigue los pasos en pantalla.
3. ¡Listo! Abre **CastDesktop HD** desde el acceso directo del Menú Inicio o Escritorio y comienza a transmitir.

*No requiere instalar Python, no requiere ejecutar comandos de pip ni configurar variables de entorno (PATH).*

---

## ⚙️ Automatización e Integración Continua (CI/CD)

El proyecto cuenta con una GitHub Action (`.github/workflows/build-installer.yml`) que automatiza el proceso completo de compilación en un entorno `windows-latest`:
- Descarga automáticamente FFmpeg (build essentials de gyan.dev).
- Publica el proyecto WPF en modo autocontenido single-file (`win-x64`).
- Empaqueta la aplicación usando Inno Setup para generar `CastDesktop_Setup.exe`.
- Publica automáticamente el instalador en los **Releases** de GitHub en cada cambio enviado a la rama `main` o ejecutado manualmente (`workflow_dispatch`).

---

## 🛠️ Compilación Manual desde el Código Fuente (Opcional)

Si deseas compilar la aplicación localmente en tu equipo:

### Requisitos
- Windows 10 / Windows 11 (64-bit).
- .NET 8.0 SDK o superior.

### Pasos de Compilación y Empaquetado

1. **Compilar el proyecto WPF:**
   ```bash
   dotnet build src/CastDesktop/CastDesktop.csproj /p:EnableWindowsTargeting=true
   ```

2. **Publicar como Ejecutable Único Autocontenido (win-x64):**
   ```bash
   dotnet publish src/CastDesktop/CastDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true /p:EnableWindowsTargeting=true
   ```

3. **Generar Instalador (Inno Setup):**
   - Coloca `ffmpeg.exe` en la carpeta de publicación `src/CastDesktop/bin/Release/net8.0-windows/win-x64/publish/`.
   - Abre `installer/setup.iss` con Inno Setup Compiler y presiona **Compile** (o ejecuta `ISCC.exe installer/setup.iss`).
   - Se generará el instalador final `installer/CastDesktop_Setup.exe`.

---

## 🖥️ Guía de Uso

1. **Abrir la aplicación CastDesktop HD.**
2. **Seleccionar Fuente:** Elige "Pantalla Completa" o "Ventana Específica".
3. **Seleccionar Chromecast:** Haz clic en `🔄 Buscar` y selecciona tu dispositivo en la lista desplegable.
4. **Seleccionar Calidad:**
   - **Alta:** 1080p/4K, 60 FPS, 35 Mbps (Mayor nitidez de texto).
   - **Media:** 1080p, 30 FPS, 18 Mbps (Equilibrada).
   - **Baja:** 720p, 30 FPS, 6 Mbps (Redes congestionadas).
5. **Transmitir:** Pulsa `▶ INICIAR TRANSMISIÓN`.
