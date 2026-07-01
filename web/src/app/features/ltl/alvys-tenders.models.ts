/**
 * TypeScript projections of the read-only Alvys tender (inbound EDI offer) resources
 * (`POST /api/alvys/tenders/search`, `GET /api/alvys/tenders/{tenderId}`). These DTOs mirror
 * Alvys's own PascalCase field names exactly (see AlvysDtos.cs) rather than the camelCase used by
 * the LTL-layer models, because the backend passes this shape through with explicit
 * `[JsonPropertyName]` overrides instead of normalizing it.
 */

export interface AlvysTendersResponse {
  Page: number;
  PageSize: number;
  Total: number;
  Items: AlvysTender[];
}

export interface AlvysTenderDateTime {
  DateTime: string;
  TimeZoneCode?: string | null;
}

export interface AlvysTenderEquipment {
  Number?: string | null;
  Length?: number | null;
  Type?: string | null;
}

export interface AlvysTenderEntity {
  Type?: string | null;
  Name?: string | null;
  CompanyName?: string | null;
  IdCodeQualifier?: string | null;
  IdCode?: string | null;
  N1Qualifier?: string | null;
  Street?: string | null;
  City?: string | null;
  PostalCode?: string | null;
  CountryCode?: string | null;
  Phone?: string | null;
  Email?: string | null;
  State?: string | null;
}

export interface AlvysTenderReference {
  Id?: string | null;
  Qualifier?: string | null;
  Description?: string | null;
}

export interface AlvysTenderOrderDetail {
  Quantity?: number | null;
  WeightUnitCode?: string | null;
  Weight?: number | null;
  ReferenceId?: string | null;
  PoNumber?: string | null;
  VolumeUnitQualifier?: string | null;
  Volume?: number | null;
  UnitBasisForMeasurement?: string | null;
  Description?: string | null;
  ReferenceId2?: string | null;
  SequenceNumber?: number | null;
}

export interface AlvysTenderStop {
  StopId: string;
  Type: string;
  Entity?: AlvysTenderEntity | null;
  SequenceNumber?: number | null;
  Orders?: AlvysTenderOrderDetail[] | null;
  References?: AlvysTenderReference[] | null;
  WeightQualifier?: string | null;
  ArrivedAt?: AlvysTenderDateTime | null;
  DepartedAt?: AlvysTenderDateTime | null;
  ScheduledArrivalStart?: AlvysTenderDateTime | null;
  ScheduledArrivalEnd?: AlvysTenderDateTime | null;
  StopReasonCode?: string | null;
  Notes?: string[] | null;
}

export interface AlvysTender {
  Id: string;
  CompanyCode?: string | null;
  Status?: string | null;
  DateImported?: AlvysTenderDateTime | null;
  ShipmentId?: string | null;
  LoadNumber?: string | null;
  Equipment?: AlvysTenderEquipment | null;
  Entities?: AlvysTenderEntity[] | null;
  PaymentMethod?: string | null;
  QtyPallets?: number | null;
  SCAC?: string | null;
  Weight?: number | null;
  WeightUnitCode?: string | null;
  Volume?: number | null;
  VolumeUnitCode?: string | null;
  Rate?: number | null;
  ExpirationDate?: AlvysTenderDateTime | null;
  Notes?: string[] | null;
  Stops?: AlvysTenderStop[] | null;
  References?: AlvysTenderReference[] | null;
  RoutingSequenceCode?: string | null;
  TransportationMethodTypeCode?: string | null;
  Etag?: string | null;
}

export interface TenderSearchFilter {
  Status?: string[];
  Type?: string;
  SourceCustomer?: string;
  LoadNumber?: string;
}

export interface TenderSearchRequest {
  Page: number;
  PageSize: number;
  Filter?: TenderSearchFilter;
}
