using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Arrivals;

/// <summary>
/// Builds the Laredo Arrivals Board (Phase 8.1) entirely from live, read-only Alvys reads: the
/// trucks/trailers with a Laredo-area stop scheduled on a given day, framed for the pilot
/// Laredo → Dallas corridor. Dallas-bound arrivals are surfaced first because they are the LTL
/// opportunity Ben/Jordan are monitoring for.
///
/// <para>
/// The Laredo / Dallas geography is single-sourced from the same
/// <see cref="ConsolidationOptions"/> the Consolidate flow uses (the <c>LAREDO_TO_DALLAS</c>
/// corridor's origin/destination warehouses), so the board never introduces a second definition
/// of "near Laredo" / "near Dallas" and stays inside the pilot radii. Read-only: nothing here
/// writes back to Alvys.
/// </para>
///
/// <para>
/// Honesty posture: an arrival window, driver name, equipment unit or ownership that Alvys does
/// not carry is surfaced as null / <see cref="ArrivalOwnership.Unknown"/> — never guessed. The
/// trip sweep is bounded by <see cref="LtlOptions.MaxLoadsScanned"/>; a capped sweep sets
/// <see cref="LaredoArrivalsBoard.Truncated"/> so the UI reports the list as a floor.
/// </para>
/// </summary>
public sealed class LaredoArrivalsService(
    IAlvysClient alvys,
    IOptions<LtlOptions> options,
    IOptions<ConsolidationOptions> consolidationOptions,
    TimeProvider clock)
{
    private readonly LtlOptions _options = options.Value;
    private readonly ConsolidationOptions _consolidation = consolidationOptions.Value;

    private const string PilotCorridorCode = "LAREDO_TO_DALLAS";

    /// <summary>Stop status tokens (case-insensitive) that read as "arrived / on-site".</summary>
    private static readonly string[] ArrivedTokens =
        ["Arrived", "Completed", "Picked Up", "Loading", "Unloading", "Checked In"];

    public async Task<LaredoArrivalsBoard> GetBoardAsync(DateOnly? date, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var day = date ?? DateOnly.FromDateTime(now.UtcDateTime);

        var (origin, destination) = ResolveCorridorWarehouses();
        if (origin is null)
        {
            // Corridor/warehouse config missing — honest empty board, no fabrication.
            return new LaredoArrivalsBoard { GeneratedAt = now, Date = day, Arrivals = [] };
        }

        var dayStart = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1).AddTicks(-1);

        var (trips, truncated) = await SweepArrivingTripsAsync(dayStart, dayEnd, ct);

        // Trips with a Laredo-area stop scheduled on the target day.
        var candidates = trips
            .Select(trip => (trip, laredo: FindWarehouseStop(trip, origin)))
            .Where(x => x.laredo is not null && StopIsOnDay(x.laredo!, x.trip, day))
            .ToList();

        // Resolve equipment master data only when there is something to enrich (avoids the cost of
        // two extra bounded sweeps when the board is empty).
        var (trucksById, trailersById) = candidates.Count == 0
            ? ([], [])
            : await BuildEquipmentIndexAsync(ct);

        var arrivals = candidates
            .Select(x => BuildArrival(x.trip, x.laredo!, origin, destination!, trucksById, trailersById, now))
            .OrderByDescending(a => a.DallasBound)
            .ThenBy(a => a.ScheduledArrivalStart ?? DateTimeOffset.MaxValue)
            .ThenBy(a => a.TripNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LaredoArrivalsBoard
        {
            GeneratedAt = now,
            Date = day,
            Yard = origin.Code,
            Arrivals = arrivals,
            Truncated = truncated,
        };
    }

    private (ConsolidationWarehouseOptions? Origin, ConsolidationWarehouseOptions? Destination) ResolveCorridorWarehouses()
    {
        var corridor = _consolidation.Corridors.FirstOrDefault(
            c => string.Equals(c.Code, PilotCorridorCode, StringComparison.OrdinalIgnoreCase));
        if (corridor is null) return (null, null);

        var byCode = _consolidation.Warehouses.ToDictionary(
            w => w.Code, w => w, StringComparer.OrdinalIgnoreCase);
        byCode.TryGetValue(corridor.OriginWarehouseCode, out var origin);
        byCode.TryGetValue(corridor.DestinationWarehouseCode, out var destination);
        return (origin, destination);
    }

    private LaredoArrival BuildArrival(
        AlvysTrip trip,
        AlvysTripStop laredo,
        ConsolidationWarehouseOptions origin,
        ConsolidationWarehouseOptions destination,
        IReadOnlyDictionary<string, AlvysTruck> trucksById,
        IReadOnlyDictionary<string, AlvysTrailerEquipment> trailersById,
        DateTimeOffset now)
    {
        var stops = OrderedStops(trip);
        var laredoIndex = stops.IndexOf(laredo);
        var onward = laredoIndex >= 0 ? stops.Skip(laredoIndex + 1).ToList() : [];

        var dallasBound = onward.Any(s => MatchesWarehouse(s.Address, destination));
        var inboundStop = stops.FirstOrDefault(
            s => string.Equals(s.StopType, "Pickup", StringComparison.OrdinalIgnoreCase)) ?? stops.FirstOrDefault();

        var scheduledStart = laredo.StopWindowStart ?? laredo.Appointment;
        var scheduledEnd = laredo.StopWindowEnd ?? laredo.Appointment;
        var status = DeriveStatus(laredo);

        var eta = EtaEstimator.Estimate(
            now,
            actualPickupAt: trip.PickedUpAt ?? trip.PickedUpDate,
            actualDeliveryAt: trip.DeliveredAt ?? trip.DeliveredDate,
            scheduledDeliveryAt: scheduledEnd ?? scheduledStart,
            delivered: (trip.DeliveredAt ?? trip.DeliveredDate) is not null,
            loadedMiles: trip.LoadedMileage?.Value,
            billingMiles: trip.TotalMileage?.Value,
            _options.Eta);

        return new LaredoArrival
        {
            TripId = trip.Id,
            TripNumber = trip.TripNumber,
            LoadNumber = trip.LoadNumber,
            OrderNumber = trip.OrderNumber,
            Truck = ResolveTruck(trip.Truck?.Id, trucksById),
            Trailer = ResolveTrailer(trip.Trailer, trailersById),
            DriverName = FirstNonBlank(trip.Driver1?.Name, trip.Driver?.Name, trip.Driver2?.Name),
            InboundFrom = PlaceFrom(inboundStop?.Address),
            Laredo = PlaceFrom(laredo.Address) ?? new ArrivalPlace(),
            ScheduledArrivalStart = scheduledStart,
            ScheduledArrivalEnd = scheduledEnd,
            ArrivedAt = laredo.ArrivedDate,
            DepartedAt = laredo.DepartedDate,
            Status = status,
            PredictedArrivalAt = eta.PredictedDeliveryAt,
            EtaBasis = eta.Basis,
            PredictedLate = eta.PredictedLate,
            DallasBound = dallasBound,
            OnwardStops = onward
                .Select(s => PlaceFrom(s.Address))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList(),
        };
    }

    private static ArrivalStatus DeriveStatus(AlvysTripStop stop)
    {
        if (stop.DepartedDate is not null) return ArrivalStatus.Departed;
        if (stop.ArrivedDate is not null) return ArrivalStatus.Arrived;
        if (!string.IsNullOrWhiteSpace(stop.Status)
            && ArrivedTokens.Any(t => stop.Status.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            return ArrivalStatus.Arrived;
        }
        return ArrivalStatus.Scheduled;
    }

    private static ArrivalEquipment? ResolveTruck(
        string? truckId, IReadOnlyDictionary<string, AlvysTruck> trucksById)
    {
        if (string.IsNullOrWhiteSpace(truckId)) return null;
        trucksById.TryGetValue(truckId, out var truck);
        var (ownership, fleetName) = OwnershipOf(truck?.Fleet);
        return new ArrivalEquipment
        {
            Id = truckId,
            Unit = truck?.TruckNum,
            FleetName = fleetName,
            Ownership = ownership,
        };
    }

    private static ArrivalEquipment? ResolveTrailer(
        AlvysTrailer? trailerRef, IReadOnlyDictionary<string, AlvysTrailerEquipment> trailersById)
    {
        if (trailerRef?.Id is not { Length: > 0 } id) return null;
        trailersById.TryGetValue(id, out var master);
        var (ownership, fleetName) = OwnershipOf(master?.Fleet);
        return new ArrivalEquipment
        {
            Id = id,
            Unit = master?.TrailerNum,
            // Prefer the trip's inline equipment type, fall back to master data.
            EquipmentType = FirstNonBlank(trailerRef.EquipmentType, master?.EquipmentType),
            LengthFeet = trailerRef.EquipmentLength,
            FleetName = fleetName,
            Ownership = ownership,
        };
    }

    /// <summary>
    /// Honest ownership from an Alvys fleet reference: a resolved fleet with a name is subsidiary
    /// <see cref="ArrivalOwnership.Fleet"/>; anything else is <see cref="ArrivalOwnership.Unknown"/>.
    /// We never guess third-party-leased — Alvys carries no determinable lease marker on this shape.
    /// </summary>
    private static (ArrivalOwnership Ownership, string? FleetName) OwnershipOf(AlvysFleet? fleet) =>
        string.IsNullOrWhiteSpace(fleet?.Name)
            ? (ArrivalOwnership.Unknown, null)
            : (ArrivalOwnership.Fleet, fleet!.Name);

    private static ArrivalPlace? PlaceFrom(AlvysAddress? address)
    {
        if (address is null) return null;
        if (string.IsNullOrWhiteSpace(address.City) && string.IsNullOrWhiteSpace(address.State))
            return null;
        return new ArrivalPlace { City = address.City, State = address.State };
    }

    private static List<AlvysTripStop> OrderedStops(AlvysTrip trip) =>
        (trip.Stops ?? [])
        .OrderBy(s => s.Sequence ?? int.MaxValue)
        .ToList();

    private static AlvysTripStop? FindWarehouseStop(AlvysTrip trip, ConsolidationWarehouseOptions warehouse) =>
        OrderedStops(trip).FirstOrDefault(s => MatchesWarehouse(s.Address, warehouse));

    /// <summary>
    /// Whether a stop address falls "near" a warehouse. Same city-whitelist / same-state fallback
    /// logic the consolidation candidate matcher uses (single pilot definition of "near"), so the
    /// board stays inside the pilot radii and never widens the corridor.
    /// </summary>
    private static bool MatchesWarehouse(AlvysAddress? address, ConsolidationWarehouseOptions warehouse)
    {
        if (address is null) return false;

        if (!string.IsNullOrWhiteSpace(address.City))
        {
            foreach (var city in warehouse.NearbyCities)
            {
                if (address.City.Contains(city, StringComparison.OrdinalIgnoreCase)) return true;
                if (city.Contains(address.City, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(address.State)
            && string.Equals(address.State, warehouse.State, StringComparison.OrdinalIgnoreCase))
        {
            if (warehouse.NearbyCities.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(address.City)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a Laredo stop belongs to the target day. Prefers the stop's own dates (scheduled
    /// window, appointment, or actual arrival); when the stop carries none, falls back to the
    /// trip's delivery/pickup date. Returns true on no date at all — the trip was already
    /// server-filtered to the day, so we don't drop it for a missing stop timestamp.
    /// </summary>
    private static bool StopIsOnDay(AlvysTripStop stop, AlvysTrip trip, DateOnly day)
    {
        var candidates = new[]
        {
            stop.ArrivedDate, stop.StopWindowStart, stop.StopWindowEnd, stop.Appointment,
            trip.DeliveryDate, trip.PickedUpAt, trip.PickupDate,
        };

        var known = candidates.Where(c => c is not null).Select(c => c!.Value).ToList();
        if (known.Count == 0) return true;
        return known.Any(c => DateOnly.FromDateTime(c.UtcDateTime) == day);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Sweeps trips delivering within the target day, bounded by <see cref="LtlOptions.MaxLoadsScanned"/>.
    /// The delivery-date window is the bounded server hint for "arriving in Laredo this day": in the
    /// Laredo → Dallas pilot the inbound truck's Laredo stop is its yard delivery leg, so the delivery
    /// date tracks the Laredo arrival date. Trips where Laredo is strictly a mid-route waypoint of a
    /// longer same-truck run are a documented limitation, not fabricated coverage.
    /// </summary>
    private async Task<(List<AlvysTrip> Items, bool Truncated)> SweepArrivingTripsAsync(
        DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;
        var items = new List<AlvysTrip>();
        var page = 0;

        while (true)
        {
            var response = await alvys.SearchTripsAsync(
                new TripSearchRequest
                {
                    Page = page,
                    PageSize = pageSize,
                    DeliveryDateRange = new AlvysDateRange { Start = dayStart, End = dayEnd },
                },
                ct);
            if (response.Items.Count == 0) break;

            items.AddRange(response.Items);

            if (items.Count >= _options.MaxLoadsScanned)
                return (items, response.Total > items.Count);
            if (items.Count >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return (items, false);
    }

    private async Task<(Dictionary<string, AlvysTruck> Trucks, Dictionary<string, AlvysTrailerEquipment> Trailers)>
        BuildEquipmentIndexAsync(CancellationToken ct)
    {
        var trucks = new Dictionary<string, AlvysTruck>(StringComparer.OrdinalIgnoreCase);
        var trailers = new Dictionary<string, AlvysTrailerEquipment>(StringComparer.OrdinalIgnoreCase);

        var pageSize = _options.AlvysPageSize;
        var page = 0;
        var scanned = 0;
        while (true)
        {
            var response = await alvys.SearchTrucksAsync(
                new TruckSearchRequest { Page = page, PageSize = pageSize }, ct);
            if (response.Items.Count == 0) break;
            foreach (var t in response.Items)
                if (!string.IsNullOrWhiteSpace(t.Id)) trucks[t.Id] = t;
            scanned += response.Items.Count;
            if (scanned >= _options.MaxLoadsScanned) break;
            if (scanned >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        page = 0;
        scanned = 0;
        while (true)
        {
            var response = await alvys.SearchTrailersAsync(
                new TrailerSearchRequest { Page = page, PageSize = pageSize }, ct);
            if (response.Items.Count == 0) break;
            foreach (var t in response.Items)
                if (!string.IsNullOrWhiteSpace(t.Id)) trailers[t.Id] = t;
            scanned += response.Items.Count;
            if (scanned >= _options.MaxLoadsScanned) break;
            if (scanned >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return (trucks, trailers);
    }
}
