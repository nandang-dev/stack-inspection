/**
 * Kontrak Stack Inspection API v1 (identik dengan DTO C# di
 * src/StackInspection.Application/Contracts). Nama field camelCase, enum sebagai union string.
 */

export type ReadStatus = 'Matched' | 'Corrected' | 'Unknown' | 'LabelNotVisible' | 'InferredByAppearance';

export type SkuClass = 'A' | 'B' | 'C';

export type ViolationType =
  | 'UNKNOWN_SKU'
  | 'LABEL_NOT_VISIBLE'
  | 'MAX_STACK_EXCEEDED'
  | 'CLASS_POSITION_INVALID';

export type CollectWarning =
  | 'LOW_DETECTION_CONFIDENCE'
  | 'UNKNOWN_SKU_PRESENT'
  | 'LABEL_NOT_VISIBLE_PRESENT'
  | 'IRREGULAR_GRID'
  | 'BACK_LAYER_EXCLUDED'
  /** Tanpa candidateSkus: ada hasil baca yang dikoreksi ke SKU mayoritas di foto (readStatus 'Corrected'). */
  | 'SKU_CORRECTED_BY_MAJORITY'
  /** Nomor SKU terbaca sebagian (label terpotong) lalu dilengkapi dari SKU yang terbaca utuh di foto / candidateSkus. */
  | 'SKU_CORRECTED_BY_PARTIAL_READ'
  /** SKU diambil dari kardus lain yang tampilannya sangat mirip (readStatus 'InferredByAppearance'). */
  | 'SKU_INFERRED_BY_APPEARANCE'
  /** Susunan sangat bercabang: stacks dipotong di 200 jalur (penilaian Analyze tidak terpengaruh). */
  | 'STACK_PATHS_TRUNCATED';

export type PhotoRejectReason =
  | 'PHOTO_RESOLUTION_TOO_LOW'
  | 'CAMERA_OVERLAY_DETECTED'
  | 'NO_CARTON_DETECTED'
  /** Ditolak gate cepat (classifier / hasil deteksi) tanpa alasan spesifik. */
  | 'FAST_REJECTED';

/** Kode SKU khusus untuk sel yang tidak terbaca. */
export type SpecialSku = 'UNKNOWN' | 'LABEL_NOT_VISIBLE';

export interface BoundingBoxDto {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

/**
 * Satu sel grid tampilan yang ditempati kardus. Kardus besar/lebar menempati beberapa sel dan muncul
 * beberapa kali dengan isi dan `id` yang sama → UI menggabungkan sel ber-id sama menjadi satu kotak.
 */
export interface CellDto {
  /** ID kardus (0..N-1, urut dari kiri-atas). Entri dengan id sama = kardus yang sama. */
  id: number;
  /** Baris grid tampilan, 0 = paling atas. */
  row: number;
  /** Kolom grid tampilan, 0 = paling kiri. */
  column: number;
  /** Level fisik di tumpukan: 0 = di lantai, selain itu 1 + level tertinggi kardus di restsOn. */
  level: number;
  /** id kardus yang langsung ditumpu (kosong = di lantai; > 1 = kardus menjembatani beberapa tumpukan). */
  restsOn: number[];
  /** Kode SKU 8 digit, atau 'UNKNOWN' / 'LABEL_NOT_VISIBLE'. */
  sku: string;
  ocrText: string | null;
  /**
   * Keyakinan OCR 0–1. Pada readStatus 'InferredByAppearance' berisi kemiripan tampilan (0–1) ke kardus acuan,
   * karena SKU-nya tidak dibaca dari label.
   */
  ocrConfidence: number;
  detectionConfidence: number;
  readStatus: ReadStatus;
  boundingBox: BoundingBoxDto;
}

/** Satu jalur tumpukan dari lantai sampai kardus teratas, mengikuti restsOn. */
export interface StackDto {
  /** Nomor jalur (0..), urut dari kiri. */
  path: number;
  /** Kolom grid tampilan kardus paling bawah di jalur ini. */
  column: number;
  height: number;
  /**
   * Kardus di jalur, urut paling bawah → paling atas. Isinya sama dengan entri cells ber-id sama
   * (kardus yang menempati beberapa sel diwakili sel kiri-atasnya).
   */
  cartons: CellDto[];
}

export interface DistinctSkuDto {
  sku: string;
  count: number;
}

export interface CollectSkuResponse {
  inspectionId: string;
  /** Nama model deteksi yang dipakai (lihat GET /api/v1/models). */
  model: string;
  imageWidth: number;
  imageHeight: number;
  rowCount: number;
  columnCount: number;
  excludedBackLayerCount: number;
  /** Urut row lalu column. Jumlah kardus = jumlah id unik, bukan cells.length. */
  cells: CellDto[];
  /** Jalur tumpukan (maks. 200). */
  stacks: StackDto[];
  /** Urut count menurun, lalu sku menaik. */
  distinctSkus: DistinctSkuDto[];
  warnings: CollectWarning[];
  processingTimeMs: number;
  /** Rincian waktu proses per tahap (ms); jumlahnya ≈ processingTimeMs. */
  timings: CollectSkuTimingsDto;
}

/** Waktu eksekusi tiap tahap pipeline Collect SKU (ms). */
export interface CollectSkuTimingsDto {
  /** Validasi upload + decode gambar. */
  decodeMs: number;
  /** Photo gate: resolusi, fast-reject classifier, cek stempel kamera. */
  inspectMs: number;
  /** Deteksi kardus + filter kotak bersarang, gate hasil deteksi, filter lapisan depan. */
  detectMs: number;
  /** OCR area label semua kardus (+ tahap 2 jika aktif), termasuk menunggu giliran OCR request lain. */
  ocrMs: number;
  /** Fuzzy match, koreksi mayoritas, potongan nomor, inferensi tampilan. */
  skuResolveMs: number;
  /** Penyusunan grid, stack, dan ringkasan response. */
  buildMs: number;
}

export interface MasterSkuDto {
  sku: string;
  name: string;
  /** Jumlah maksimal kardus SKU yang sama per jalur tumpukan (> 0). */
  maxStack: number;
  weight: number;
  class: SkuClass;
}

export interface AnalyzeStackRequest {
  /** Response Collect SKU apa adanya; hasil versi lama (cells tanpa id/restsOn) ditolak 400. */
  collectResult: CollectSkuResponse;
  masterSkus: MasterSkuDto[];
}

/** Pemakaian token LLM (salin dari field usage respons LLM). */
export interface TokenUsageDto {
  /** Token input (prompt + gambar), ≥ 0. */
  inputTokens: number;
  /** Token output, ≥ 0. */
  outputTokens: number;
  /** inputTokens + outputTokens; dihitung server, diabaikan jika dikirim. */
  totalTokens?: number;
}

/**
 * Query POST /api/v1/stack-inspections/preview-from-result. Body = CollectSkuResponse apa adanya
 * (response: HTML lampiran).
 */
export interface StackPreviewFromResultRequest {
  /** Opsional: JSON array MasterSkuDto[] sebagai string; diisi = skor & pelanggaran ditampilkan. */
  masterSkus?: string | null;
  /** Opsional, ≥ 0: token input LLM. */
  inputTokens?: number | null;
  /** Opsional, ≥ 0: token output LLM. */
  outputTokens?: number | null;
}

export interface ViolationDto {
  type: ViolationType;
  /** id kardus yang melanggar → sorot semua cells dengan id ini. */
  cartonId: number;
  /** Sel grid tampilan kiri-atas kardus. */
  row: number;
  column: number;
  level: number;
  sku: string;
  message: string;
}

export interface SkuSummaryDto {
  sku: string;
  /** null jika SKU tidak ada di master. */
  name: string | null;
  /** null jika SKU tidak ada di master. */
  class: SkuClass | null;
  count: number;
  violationCount: number;
}

/** Ringkasan satu jalur tumpukan (bawah → atas mengikuti restsOn). */
export interface StackSummaryDto {
  path: number;
  /** Kolom grid tampilan kardus paling bawah di jalur ini. */
  column: number;
  height: number;
  /** Kardus di jalur, urut paling bawah → paling atas. */
  cartons: StackSummaryCartonDto[];
  violationCount: number;
}

/** Satu kardus di jalur tumpukan hasil Analyze: data kardus seperti cells Collect SKU + pelanggarannya. */
export interface StackSummaryCartonDto extends CellDto {
  /** Jenis pelanggaran kardus ini (kosong = tidak melanggar). */
  violations: ViolationType[];
}

export interface AnalyzeStackResponse {
  inspectionId: string;
  totalScore: number;
  maxPossibleScore: number;
  percentageScore: number;
  /** Urut row, column, lalu type. */
  violations: ViolationDto[];
  skuSummary: SkuSummaryDto[];
  stacks: StackSummaryDto[];
}

export interface ModelMetricsDto {
  precision: number;
  recall: number;
  map50: number;
  map50To95: number;
}

/** Item GET /api/v1/models. */
export interface ModelInfoDto {
  /** Nama model, dipakai di field form `model` Collect SKU. */
  name: string;
  isDefault: boolean;
  inputSize: number;
  confidenceThreshold: number;
  iouThreshold: number;
  /** 'candidate' | 'approved' | 'rejected' dari Carton Trainer; null jika tanpa model card. */
  status: string | null;
  baseModel: string | null;
  datasetVersion: string | null;
  trainedAt: string | null;
  metrics: ModelMetricsDto | null;
}

/** Field form multipart Collect SKU. */
export interface CollectSkuForm {
  image: File;
  /**
   * Daftar SKU untuk koreksi hasil OCR, disarankan seluruh master SKU. Jika kosong, API mengoreksi
   * salah baca ke SKU mayoritas di foto (warning SKU_CORRECTED_BY_MAJORITY).
   */
  candidateSkus?: string[];
  /** Kosong = model default. */
  model?: string;
}

/** Body error 422 (ProblemDetails). */
export interface PhotoNotInspectableProblem {
  type: string;
  title: string;
  status: 422;
  detail: string;
  code: 'PHOTO_NOT_INSPECTABLE';
  reason: PhotoRejectReason;
}

/** Body error 400 (ValidationProblemDetails). */
export interface ValidationProblem {
  type?: string;
  title: string;
  status: 400;
  errors: Record<string, string[]>;
}
