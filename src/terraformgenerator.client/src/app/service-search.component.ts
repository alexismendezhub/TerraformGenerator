import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, inject, output, signal, viewChild } from '@angular/core';
import { GENERIC_PREFIX, ResourceSummary, groupOf } from './models';
import { OtherResourceComponent } from './other-resource.component';
import { SketchStore } from './sketch.store';

// Every resource of the cloud's Terraform provider, found by name, in a window over the drawing: Add puts it on the
// drawing, the pin keeps it in the palette (in its section, or OTHERS).
@Component({
  selector: 'app-service-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="backdrop" (click)="closed.emit()"></div>
    <section class="dialog wide" role="dialog" aria-modal="true" aria-labelledby="search-title">
      <h2 id="search-title">Search {{ store.platform()?.label ?? 'cloud' }} services</h2>
      <p class="hint">Every resource of the official Terraform provider. Add puts it on the drawing; the pin keeps it in the palette.</p>
      <div class="list-status">
        <span class="hint small">
          @if (store.schemaRefreshing()) {
            Downloading the newest provider list (up to a minute)…
          } @else if (store.schemaStatus(); as status) {
            @if (status.ready) {
              {{ status.count }} services{{ status.version ? ' · provider ' + status.version : '' }}{{ status.updatedUtc ? ' · updated ' + (status.updatedUtc | date: 'mediumDate') : '' }}
            }
            @if (status.problem) {
              <span class="problem">The list could not be updated: {{ status.problem }}</span>
            }
          }
        </span>
        <button type="button" [disabled]="store.schemaRefreshing()" (click)="refresh()">Refresh services</button>
      </div>
      <input #box type="search" autocomplete="off" spellcheck="false" placeholder="For example bucket, queue, cluster, gateway"
             [value]="query()" (input)="search(value($event))" />
      @if (searching()) {
        <p class="hint small">{{ firstTime() ? 'Loading the full list the first time (up to a minute)…' : 'Searching…' }}</p>
      }
      <ul class="results">
        @for (result of results(); track result.type) {
          <li>
            <span class="chip">{{ result.short }}</span>
            <span class="text">{{ result.title }}<small>{{ result.type }} · {{ store.sectionName(section(result.type)) }}</small></span>
            <button type="button" class="pin" [class.on]="store.isPinned(result.type)" [attr.aria-pressed]="store.isPinned(result.type)"
                    [title]="store.isPinned(result.type) ? 'Unpin from the palette' : 'Pin to the palette'" (click)="store.togglePin(result.type)">
              {{ store.isPinned(result.type) ? 'Pinned' : 'Pin' }}</button>
            <button type="button" [disabled]="store.previewing()" (click)="add(result)">Add</button>
          </li>
        }
      </ul>
      @if (query().trim().length > 1 && !searching() && results().length === 0) {
        <p class="hint">Nothing found. Try another word, or describe it below.</p>
      }
      <app-other-resource (proposed)="closed.emit()" />
      <div class="buttons">
        <button type="button" (click)="closed.emit()">Close</button>
      </div>
    </section>
  `,
  styleUrl: './dialog.css',
  styles: `
    .dialog.wide { width: min(760px, calc(100vw - 32px)); max-height: 80vh; overflow: auto; }
    input[type='search'] { width: 100%; padding: 9px 10px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); color: var(--text); font: inherit; }
    .results { margin: 10px 0 0; padding: 0; list-style: none; max-height: 46vh; overflow: auto; }
    .results li { display: flex; gap: 8px; align-items: center; padding: 6px 4px; border-bottom: 1px solid var(--border); }
    .text { display: flex; flex: 1; flex-direction: column; min-width: 0; font-size: 14px; }
    .text small { overflow: hidden; color: var(--muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
    .chip { display: inline-grid; flex: none; place-items: center; width: 38px; height: 24px; border-radius: 5px; background: #64748b; color: #fff; font-size: 10px; font-weight: 700; }
    .pin.on { border-color: var(--accent); color: var(--accent); }
    .list-status { display: flex; gap: 8px; align-items: center; justify-content: space-between; margin: 0 0 8px; }
    .list-status .problem { display: block; color: var(--error); }
  `,
  imports: [DatePipe, OtherResourceComponent],
})
export class ServiceSearchComponent {
  protected readonly store = inject(SketchStore);
  readonly closed = output<void>();
  protected readonly query = signal('');
  protected readonly results = signal<ResourceSummary[]>([]);
  protected readonly searching = signal(false);
  protected readonly firstTime = signal(true);
  private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');
  private timer: ReturnType<typeof setTimeout> | undefined;
  private sequence = 0;

  constructor() {
    afterNextRender(() => this.box().nativeElement.focus());
    void this.store.loadSchemaStatus();
  }

  // Asks for the newest list, then runs the current search again on it.
  protected async refresh(): Promise<void> {
    await this.store.refreshSchemas();
    this.firstTime.set(false);
    this.search(this.query());
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected section(type: string): string {
    return groupOf(type, this.store.sketch().settings.platform ?? 'azure');
  }

  protected search(query: string): void {
    this.query.set(query);
    clearTimeout(this.timer);
    if (query.trim().length < 2) {
      this.results.set([]);
      return;
    }

    this.timer = setTimeout(async () => {
      const sequence = ++this.sequence;
      this.searching.set(true);
      try {
        const results = await this.store.searchResources(query.trim());
        if (sequence === this.sequence) {
          this.results.set(results);
          this.firstTime.set(false);
        }
      } finally {
        if (sequence === this.sequence) {
          this.searching.set(false);
        }
      }
    }, 250);
  }

  protected add(result: ResourceSummary): void {
    this.store.addNode(GENERIC_PREFIX + result.type);
    this.closed.emit();
  }
}
