# Stack Inspection API

API untuk menilai kualitas tumpukan kardus di truk/kontainer dari sebuah foto (brief:
`../brief/BRIEF-stack-inspection-api-v2.md`).

1. `POST /api/v1/stack-inspections/collect-sku` — cek kelayakan foto, deteksi kardus lapisan depan (YOLOX
   ONNX dari Carton Trainer), baca SKU (PaddleOCR PP-OCRv5), susun baris/kolom.
   Field opsional `model` memilih model deteksi (dropdown di Swagger); kosong = model default.
2. `GET /api/v1/models` — daftar model deteksi yang tersedia (nama, input size, threshold, status, metrik).
3. `POST /api/v1/stack-inspections/analyze` — hitung pelanggaran (`UNKNOWN_SKU`, `LABEL_NOT_VISIBLE`,
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
dotnet test           # 66 test, memakai fake vision; tidak butuh model/library native
```

Di Mac, library native OpenCvSharp ≥ 4.11 untuk Apple Silicon tidak tersedia di NuGet, jadi vision asli
**tidak dijalankan di Mac**. Unit test dan `Vision:UseFake = true` (lihat `appsettings.Development.json`,
tidak di-commit) cukup untuk development. Vision asli diuji di Linux (Docker) dan Windows.

## Konfigurasi utama (`appsettings.json`)

| Key | Default | Keterangan |
|---|---|---|
| `Vision:UseFake` | `false` | `true` = fake vision dari `Vision:FakeFixturePath` |
| `Vision:ModelsDirectory` | `models` (Docker: `/app/models`) | Folder model **di luar repo**, satu subfolder per model |
| `Vision:DefaultModel` | `carton-v1` | Model jika request tidak memilih `model` |
| `Vision:InputSize` / `ConfidenceThreshold` / `IouThreshold` | 960 / 0.5 / 0.5 | Default jika `model-card.json` tidak mencantumkannya |
| `Vision:Threads` | 4 | Thread ONNX Runtime dan PaddleOCR (samakan dengan jumlah vCPU) |
| `Vision:ContainmentThreshold` | 0.8 | Kotak yang ≥80% luasnya berada di dalam kotak lain dibuang sebagai duplikat (0 = nonaktif) |
| `Photo:MinLongSide` | 3000 | Gate resolusi (sisi terpanjang, px) |
| `Photo:OverlayKeywords` | GPS Map Camera, Timemark, Lat, Long, Kode Foto | Gate stempel kamera |
| `FrontLayer:MinWidthRatio` / `MaxGapRatio` | 0.6 / 0.35 | Filter lapisan belakang |
| `FrontLayer:MinTopHeightRatio` | 0.55 | Kotak teratas tumpukan yang tingginya < rasio ini × kardus di bawahnya dianggap lapisan belakang (0 = nonaktif) |
| `Upload:MaxFileSizeMb` | 10 | Batas upload |

## Folder model

File model **tidak di-commit** dan **tidak ikut di image** (sesuai brief). Lokasinya dari `Vision:ModelsDirectory`:

```
<Vision:ModelsDirectory>/
  carton-v1/
    carton-v1.onnx        ← hasil export Carton Trainer
    model-card.json       ← inputSize, recommendedThresholds, metrik, status
  carton-v2/
    ...
```

- Nama subfolder = nama model di `GET /api/v1/models`, dropdown Swagger, dan field `model` Collect SKU.
- Nama file `.onnx` diambil dari `model-card.json` (`onnx.file`); tanpa model card dipakai file `.onnx` pertama,
  dengan input size dan threshold default `Vision:*`.
- Model default: `Vision:DefaultModel` (misalnya env `Vision__DefaultModel=carton-v2`).
- Folder dipindai saat aplikasi start → setelah menambah/mengganti model, **restart** aplikasi.
- Session ONNX dibuat saat model pertama kali dipakai lalu disimpan di memori (±100–200 MB per model).

Menambah model baru: salin `carton-vN.onnx` dan `model-card.json` dari
`carton-trainer/registry/models/carton-vN/` ke `<ModelsDirectory>/carton-vN/`, lalu restart.

## Format model (YOLOX dari Carton Trainer)

Input `images` `[1,3,S,S]` float32 BGR **tanpa normalisasi**, letterbox di **kiri atas** dengan padding 114.
Output `output` `[1,N,6]` = `cx, cy, w, h, objectness, classScore` (piksel kanvas); skor = objectness × classScore.
Implementasi: `src/StackInspection.Infrastructure/Vision` (`LetterboxGeometry`, `YoloxPostprocessor`) —
diuji terhadap nilai referensi Python Carton Trainer.

## Publish

Runtime native dipilih otomatis sesuai target (paket runtime ada di project Api dengan kondisi `RuntimeIdentifier`).
Jika restore dijalankan terpisah, sertakan `-p:RuntimeIdentifier=<rid>`; tanpa itu library native tidak ikut dan API
gagal dengan `Unable to load shared library 'paddle_inference_c'`.

```bash
dotnet publish src/StackInspection.Api -c Release -r linux-x64 --self-contained false -o out/linux
dotnet publish src/StackInspection.Api -c Release -r win-x64   --self-contained false -o out/win
```

Model tidak ikut ter-publish: siapkan folder model di server dan arahkan `Vision:ModelsDirectory` ke sana.

### Windows Server + IIS

1. Pasang **ASP.NET Core 8 Hosting Bundle**.
2. Publish `-r win-x64`, salin ke folder situs. Siapkan folder model, misalnya `D:\StackInspection\models\carton-v1\`,
   lalu set `Vision__ModelsDirectory=D:\StackInspection\models` (environment variable app pool / `web.config`).
3. App pool: **No Managed Code**, **Enable 32-Bit Applications = False** (library native hanya 64-bit).
4. CPU harus mendukung AVX2 (runtime Paddle MKL). Jika tidak, ganti ke `Sdcb.PaddleInference.runtime.win64.openblas-noavx`.
5. ⚠️ Belum diuji di Windows: `onnxruntime.dll` milik Paddle tertimpa versi 1.30 milik Microsoft.ML.OnnxRuntime
   saat publish; perlu uji start-up + OCR di server Windows.

### Linux (Docker / Coolify)

```bash
docker build -t stack-inspection-api .
docker run -p 8080:8080 -v /data/stack-inspection/models:/app/models:ro stack-inspection-api
```

- Image berbasis `mcr.microsoft.com/dotnet/aspnet:8.0` + `libgomp1`. OpenCV memakai varian **slim** (tanpa GUI),
  sehingga tidak butuh GTK/X11.
- Model tidak ikut di image: pasang folder model ke `/app/models` (Coolify: *Persistent Storage*, read-only),
  lalu salin file model ke folder host lewat SSH/SFTP (`scp`).
- CPU harus mendukung **AVX2** (runtime Paddle MKL): cek `grep -c avx2 /proc/cpuinfo`.
- Port 8080, health check `GET /health`.

## Status

| Item | Status |
|---|---|
| Kedua endpoint dengan fake vision, sesuai fixture (41.67) | ✅ |
| Unit + integration test | ✅ 66 lulus |
| Swagger contoh 200 (kedua endpoint), request Analyze, 422 | ✅ |
| DTO C# ↔ TypeScript | ✅ |
| Vision asli di Linux (Coolify) | ⏳ menunggu deploy |
| Vision asli di Windows + IIS | ⏳ belum diuji |
