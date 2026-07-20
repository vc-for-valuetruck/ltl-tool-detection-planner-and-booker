import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { LtlService } from './ltl.service';
import { LtlLoadSummary, LtlPlace } from './ltl.models';
import { LtlNav } from './ltl-nav';
import { YardArtifactFileView, YardArtifactView } from './yard-artifacts.models';

/**
 * Internal, read-only load-detail view (issue #104). Replaces the old `va336.alvys.com/loads/{n}`
 * deep-link, which 404'd because Alvys has no per-tenant subdomain and no stable public deep-link
 * by load number. Backed by `GET /api/ltl/loads/{loadNumber}` (the normalized LtlLoadSummary).
 * A secondary "Open Alvys" link points at the Alvys home (app.alvys.com) — honest about the fact
 * that the dispatcher must search the load number there, rather than pretending a deep link exists.
 * Nothing writes back to Alvys.
 */
@Component({
  selector: 'app-ltl-load-detail',
  standalone: true,
  imports: [DatePipe, RouterLink, LtlNav],
  templateUrl: './ltl-load-detail.html',
  styleUrls: ['./ltl-worklist.css', './ltl-load-detail.css'],
})
export class LtlLoadDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly ltl = inject(LtlService);

  protected readonly loadNumber = signal<string>('');
  protected readonly load = signal<LtlLoadSummary | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  /** Yard artifacts (Phase 8.2) attached to this load number. Internal dock data, never Alvys. */
  protected readonly artifacts = signal<YardArtifactView[]>([]);

  /** Alvys home — NOT a per-load deep link (none exists publicly). Dispatcher searches by number. */
  protected readonly alvysHomeUrl = 'https://app.alvys.com/';

  protected readonly hasLoad = computed(() => this.load() !== null);

  ngOnInit(): void {
    this.loadNumber.set(this.route.snapshot.paramMap.get('loadNumber') ?? '');
    this.fetch();
  }

  protected fetch(): void {
    const ref = this.loadNumber();
    if (!ref) {
      this.error.set('No load number in the URL.');
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.ltl.getLoad(ref).subscribe({
      next: (load) => {
        this.load.set(load);
        this.loading.set(false);
        this.fetchArtifacts(load.loadNumber ?? ref);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.loading.set(false);
      },
    });
  }

  private fetchArtifacts(loadNumber: string): void {
    if (!loadNumber) return;
    this.ltl.yardArtifacts({ loadNumber }).subscribe({
      next: (artifacts) => this.artifacts.set(artifacts),
      // Yard artifacts are supplementary; a failure here must never blank out the load detail.
      error: () => this.artifacts.set([]),
    });
  }

  protected fileUrl(artifactId: string, file: YardArtifactFileView): string {
    return this.ltl.yardArtifactFileUrl(artifactId, file.id);
  }

  protected photos(artifact: YardArtifactView): YardArtifactFileView[] {
    return artifact.files.filter((f) => f.kind === 'Photo');
  }

  protected pdfs(artifact: YardArtifactView): YardArtifactFileView[] {
    return artifact.files.filter((f) => f.kind === 'Pdf');
  }

  protected artifactChipClass(status: string): string {
    if (status === 'Passed') return 'chip chip-good';
    if (status === 'Flagged') return 'chip chip-danger';
    return 'chip chip-neutral';
  }

  protected verifiedDims(a: YardArtifactView): string | null {
    const v = a.verifiedPallets;
    if (!v) return null;
    const dims = [v.lengthInches, v.widthInches, v.heightInches];
    if (dims.some((d) => d === null || d === undefined)) return null;
    return `${dims[0]}×${dims[1]}×${dims[2]} in`;
  }

  protected place(p: LtlPlace | null): string {
    if (!p) return '—';
    return p.label ?? ([p.city, p.state, p.zip].filter(Boolean).join(', ') || '—');
  }

  protected formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }

  protected formatNumber(value: number | null | undefined, suffix = ''): string {
    if (value === null || value === undefined) return '—';
    return `${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}${suffix}`;
  }

  /** Weight is optional on many Alvys loads — surface "unknown", never 0. */
  protected weightLabel(load: LtlLoadSummary): string {
    return load.weightLbs === null || load.weightLbs === undefined
      ? 'Unknown'
      : `${load.weightLbs.toLocaleString(undefined, { maximumFractionDigits: 0 })} lb`;
  }

  protected badgeClass(badge: string): string {
    if (badge === 'ReadyToBill') return 'chip chip-good';
    if (badge === 'AlreadyInvoiced') return 'chip chip-neutral';
    if (badge === 'ExceptionBlockingBilling') return 'chip chip-danger';
    return 'chip chip-warn';
  }
}
