# Stack Inspection API

API untuk menilai kualitas tumpukan kardus di truk/kontainer dari sebuah foto (brief:
`../brief/BRIEF-stack-inspection-api-v2.md`).

1. `POST /api/v1/stack-inspections/collect-sku` — cek kelayakan foto, deteksi kardus lapisan depan (YOLOX
   ONNX dari Carton Trainer), baca SKU (PaddleOCR PP-OCRv5), susun baris/kolom.
2. `POST /api/v1/stack-inspections/analyze` — hitung pelanggaran (`UNKNOWN_SKU`, `LABEL_NOT_VISIBLE`,
   `MAX_STACK_EXCEEDED`, `CLASS_POSITION_INVALID`) dan skor.

Swagger UI: `/swagger` (aktif jika `Swagger:Enabled = true`). Health check: `/health`.

## Struktur

```
src/StackInspection.Domain          aturan & skor (tanpa dependency vision)
src/StackInspection.Application     use case, photo gate, DTO, interface vision
src/StackInspection.Infrastructure  ONNX Runtime + OpenCvSharp + PaddleOCR, fake vision
src/StackInspection.Api             controller, ProblemDetails, Swagger
tests/*                             xUnit (Domain, Application, Infrastructure, Api integration)
fixtures/reference-grid.json        fixture fake vision (grid 8×6 dari brief, skor 41.67)
contracts/stackInspection.types.ts  kontrak TypeScript (identik dengan DTO C#)
```

## Development (Mac / Linux / Windows)

```bash
dotnet build          # tanpa warning (TreatWarningsAsErrors)
dotnet test           # 57 test, memakai fake vision; tidak butuh model/library native
```

Di Mac, library native OpenCvSharp ≥ 4.11 untuk Apple Silicon tidak tersedia di NuGet, jadi vision asli
**tidak dijalankan di Mac**. Unit test dan `Vision:UseFake = true` (lihat `appsettings.Development.json`,
tidak di-commit) cukup untuk development. Vision asli diuji di Linux (Docker) dan Windows.

## Konfigurasi utama (`appsettings.json`)

| Key | Default | Keterangan |
|---|---|---|
| `Vision:UseFake` | `false` | `true` = fake vision dari `Vision:FakeFixturePath` |
| `Vision:DetectorModelPath` | `models/carton-v1.onnx` | File `.onnx` hasil export Carton Trainer (tidak di-commit) |
| `Vision:InputSize` / `ConfidenceThreshold` / `IouThreshold` | 960 / 0.5 / 0.5 | Ambil dari `appsettings-snippet.json` Carton Trainer |
| `Vision:Threads` | 4 | Thread ONNX Runtime |
| `Photo:MinLongSide` | 3000 | Gate resolusi (sisi terpanjang, px) |
| `Photo:OverlayKeywords` | GPS Map Camera, Timemark, Lat, Long, Kode Foto | Gate stempel kamera |
| `FrontLayer:MinWidthRatio` / `MaxGapRatio` | 0.6 / 0.35 | Filter lapisan belakang |
| `Upload:MaxFileSizeMb` | 10 | Batas upload |

## Format model (YOLOX dari Carton Trainer)

Input `images` `[1,3,S,S]` float32 BGR **tanpa normalisasi**, letterbox di **kiri atas** dengan padding 114.
Output `output` `[1,N,6]` = `cx, cy, w, h, objectness, classScore` (piksel kanvas); skor = objectness × classScore.
Implementasi: `src/StackInspection.Infrastructure/Vision` (`LetterboxGeometry`, `YoloxPostprocessor`) —
diuji terhadap nilai referensi Python Carton Trainer.

## Publish

Runtime native dipilih otomatis sesuai target:

```bash
dotnet publish src/StackInspection.Api -c Release -r linux-x64 --self-contained false -o out/linux
dotnet publish src/StackInspection.Api -c Release -r win-x64   --self-contained false -o out/win
```

Salin model ke `<folder publish>/models/carton-v1.onnx` (atau atur `Vision:DetectorModelPath`).

### Windows Server + IIS

1. Pasang **ASP.NET Core 8 Hosting Bundle**.
2. Publish `-r win-x64`, salin ke folder situs, taruh model di `models\`.
3. App pool: **No Managed Code**, **Enable 32-Bit Applications = False** (library native hanya 64-bit).
4. CPU harus mendukung AVX2 (runtime Paddle MKL). Jika tidak, ganti ke `Sdcb.PaddleInference.runtime.win64.openblas-noavx`.
5. ⚠️ Belum diuji di Windows: `onnxruntime.dll` milik Paddle tertimpa versi 1.30 milik Microsoft.ML.OnnxRuntime
   saat publish; perlu uji start-up + OCR di server Windows.

### Linux (Docker / Coolify)

```bash
docker build -t stack-inspection-api .
docker run -p 8080:8080 -v /srv/stack-inspection/models:/app/models:ro stack-inspection-api
```

- Image berbasis `mcr.microsoft.com/dotnet/aspnet:8.0` + `libgomp1`. OpenCV memakai varian **slim** (tanpa GUI),
  sehingga tidak butuh GTK/X11.
- Model tidak ikut di image: taruh `carton-v1.onnx` di volume `/app/models` (Coolify: *Persistent Storage*).
- CPU harus mendukung **AVX2** (runtime Paddle MKL): cek `grep -c avx2 /proc/cpuinfo`.
- Port 8080, health check `GET /health`.

## Status

| Item | Status |
|---|---|
| Kedua endpoint dengan fake vision, sesuai fixture (41.67) | ✅ |
| Unit + integration test | ✅ 57 lulus |
| Swagger contoh 200 (kedua endpoint), request Analyze, 422 | ✅ |
| DTO C# ↔ TypeScript | ✅ |
| Vision asli di Linux (Coolify) | ⏳ menunggu deploy |
| Vision asli di Windows + IIS | ⏳ belum diuji |
