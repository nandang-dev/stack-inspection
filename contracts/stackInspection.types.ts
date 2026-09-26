/**
 * Kontrak Stack Inspection API v1 (identik dengan DTO C# di
 * src/StackInspection.Application/Contracts). Nama field camelCase, enum sebagai union string.
 */

export type ReadStatus = 'Matched' | 'Corrected' | 'Unknown' | 'LabelNotVisible';

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
  | 'BACK_LAYER_EXCLUDED';

export type PhotoRejectReason =
  | 'PHOTO_RESOLUTION_TOO_LOW'
  | 'CAMERA_OVERLAY_DETECTED'
  | 'NO_CARTON_DETECTED';

/** Kode SKU khusus untuk sel yang tidak terbaca. */
export type SpecialSku = 'UNKNOWN' | 'LABEL_NOT_VISIBLE';

export interface BoundingBoxDto {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

export interface CellDto {
  /** Baris foto, 0 = paling atas. */
  row: number;
  /** Kolom, 0 = paling kiri. Satu kolom = satu stack. */
  column: number;
  /** Posisi dalam kolom, 0 = kardus paling bawah. */
  level: number;
  /** Kode SKU 8 digit, atau 'UNKNOWN' / 'LABEL_NOT_VISIBLE'. */
  sku: string;
  ocrText: string | null;
  ocrConfidence: number;
  detectionConfidence: number;
  readStatus: ReadStatus;
  boundingBox: BoundingBoxDto;
}

export interface StackDto {
  column: number;
  height: number;
  skusBottomToTop: string[];
}

export interface DistinctSkuDto {
  sku: string;
  count: number;
}

export interface CollectSkuResponse {
  inspectionId: string;
  imageWidth: number;
  imageHeight: number;
  rowCount: number;
  columnCount: number;
  excludedBackLayerCount: number;
  cells: CellDto[];
  stacks: StackDto[];
  /** Urut count menurun, lalu sku menaik. */
  distinctSkus: DistinctSkuDto[];
  warnings: CollectWarning[];
  processingTimeMs: number;
}

export interface MasterSkuDto {
  sku: string;
  name: string;
  /** Jumlah maksimal kardus SKU yang sama per kolom (> 0). */
  maxStack: number;
  weight: number;
  class: SkuClass;
}

export interface AnalyzeStackRequest {
  collectResult: CollectSkuResponse;
  masterSkus: MasterSkuDto[];
}

export interface ViolationDto {
  type: ViolationType;
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

export interface StackSummaryDto {
  column: number;
  height: number;
  violationCount: number;
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
