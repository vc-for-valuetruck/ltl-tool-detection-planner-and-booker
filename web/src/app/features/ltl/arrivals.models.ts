/**
 * TypeScript projections of the Phase 8.1 Laredo Arrivals Board API
 * (see src/LtlTool.Api/Features/Ltl/Arrivals/ArrivalsModels.cs). Enums serialize by name
 * (JsonStringEnumConverter). Every window/driver/equipment/ownership value is nullable on
 * purpose — `null` means "Alvys did not supply it" and is rendered as "—", never guessed.
 */

export type ArrivalStatus = 'Scheduled' | 'Arrived' | 'Departed';

export type ArrivalOwnership = 'Unknown' | 'Fleet' | 'ThirdPartyLeased';

export interface ArrivalPlace {
  city: string | null;
  state: string | null;
  /** "City, ST" / partial / null — computed server-side. */
  label: string | null;
}

export interface ArrivalEquipment {
  id: string;
  unit: string | null;
  equipmentType: string | null;
  lengthFeet: number | null;
  fleetName: string | null;
  ownership: ArrivalOwnership;
}

export interface LaredoArrival {
  tripId: string;
  tripNumber: string | null;
  loadNumber: string | null;
  orderNumber: string | null;
  truck: ArrivalEquipment | null;
  trailer: ArrivalEquipment | null;
  driverName: string | null;
  inboundFrom: ArrivalPlace | null;
  laredo: ArrivalPlace;
  scheduledArrivalStart: string | null;
  scheduledArrivalEnd: string | null;
  arrivedAt: string | null;
  departedAt: string | null;
  status: ArrivalStatus;
  predictedArrivalAt: string | null;
  etaBasis: string | null;
  predictedLate: boolean;
  dallasBound: boolean;
  onwardStops: ArrivalPlace[];
}

export interface LaredoArrivalsBoard {
  generatedAt: string;
  date: string;
  yard: string;
  arrivals: LaredoArrival[];
  truncated: boolean;
  source: string;
}
