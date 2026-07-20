/**
 * TypeScript projections of the Phase 8.2 yard-artifact intake API
 * (see src/LtlTool.Api/Features/Ltl/YardArtifacts/YardArtifactModels.cs). Enums serialize by name
 * (JsonStringEnumConverter). Verified pallet/dims are nullable on purpose — `null` means the dock
 * worker did not verify it, never guessed. This is our internal data; nothing here touches Alvys.
 */

export type YardInspectionStatus = 'Submitted' | 'Passed' | 'Flagged';

export type YardArtifactFileKind = 'Photo' | 'Pdf';

export interface YardVerifiedPallets {
  palletCount: number | null;
  lengthInches: number | null;
  widthInches: number | null;
  heightInches: number | null;
  /** Always "yard verification" — provenance label so this is distinguishable from EDI estimates. */
  source: string;
}

export interface YardArtifactFileView {
  id: string;
  kind: YardArtifactFileKind;
  fileName: string;
  contentType: string;
  sizeBytes: number;
}

export interface YardArtifactView {
  id: string;
  yard: string;
  truckUnit: string | null;
  trailerUnit: string | null;
  loadNumber: string | null;
  submittedBy: string;
  capturedAt: string;
  createdAt: string;
  status: YardInspectionStatus;
  passedItems: number;
  failedItems: number;
  naItems: number;
  verifiedPallets: YardVerifiedPallets | null;
  files: YardArtifactFileView[];
}

/** Filter for surfacing artifacts by equipment unit / load number / yard. */
export interface YardArtifactQuery {
  loadNumber?: string;
  truckUnit?: string;
  trailerUnit?: string;
  yard?: string;
  max?: number;
}
