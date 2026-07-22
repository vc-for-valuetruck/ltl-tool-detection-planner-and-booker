/**
 * Client-side types for BOL intelligence. Mirrors `/api/ltl/.../bol/*`. Enums are serialized as
 * strings by the API.
 *
 * Guardrails (do not soften in the UI): a BOL suggestion is a suggestion only. It is NEVER
 * auto-applied and NEVER written back to Alvys — a human accepts each field, and an accepted value
 * annotates internal surfaces only. `confidence` is an extraction score, never an operational number.
 */

export type BolField =
  | 'PalletCount'
  | 'PieceCount'
  | 'Weight'
  | 'FreightClass'
  | 'CommodityDescription'
  | 'HazmatFlag';

export type BolSuggestionStatus = 'Pending' | 'Accepted' | 'Rejected';

export interface BolFieldSuggestionView {
  id: string;
  loadNumber: string;
  documentId: string;
  documentName?: string | null;
  field: BolField;
  value: string;
  confidence: number;
  evidenceQuote: string;
  extractorName: string;
  status: BolSuggestionStatus;
  createdBy: string;
  createdAt: string;
  decidedAt?: string | null;
  decidedBy?: string | null;
}

export interface BolReadResponse {
  loadNumber: string;
  documentId: string;
  extractorName: string;
  count: number;
  suggestions: BolFieldSuggestionView[];
}

/** Honest snapshot of which text + field extractor is active (deterministic vs. cloud OCR stub). */
export interface BolExtractorStatus {
  textExtractor: string;
  fieldExtractor: string;
}

/** Minimal projection of an Alvys load document, from `GET /api/alvys/loads/{n}/documents`. */
export interface AlvysLoadDocument {
  id: string;
  attachmentPath?: string | null;
  attachmentType?: string | null;
  attachmentSize?: number | null;
  uploadedAt?: string | null;
  uploadedBy?: string | null;
}
