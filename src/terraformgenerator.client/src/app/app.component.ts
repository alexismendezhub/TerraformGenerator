import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, effect, inject, signal, viewChild } from '@angular/core';
import { AdviceComponent } from './advice.component';
import { CanvasComponent } from './canvas.component';
import { DescribeComponent } from './describe.component';
import { ImportComponent } from './import.component';
import { ExampleName, GENERIC_PREFIX, OTHERS, groupOf } from './models';
import { OutputComponent } from './output.component';
import { PropertiesComponent } from './properties.component';
import { ProposalComponent } from './proposal.component';
import { ServiceSearchComponent } from './service-search.component';
import { SketchStore, summarize } from './sketch.store';

// A button of the palette: a smart box, or a provider service the person pinned.
interface PaletteItem {
  kind: string;
  short: string;
  label: string;
  description: string;
  pinned: string | null;
}

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CanvasComponent, PropertiesComponent, OutputComponent, DescribeComponent, ProposalComponent, AdviceComponent, ImportComponent, ServiceSearchComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent {
  protected readonly store = inject(SketchStore);
  protected readonly tab = signal<'properties' | 'code' | 'advice'>('properties');
  protected readonly notice = signal<string | null>(null);
  protected readonly dialog = signal<'describe' | 'import' | 'search' | null>(null);
  // The section whose name is being changed.
  protected readonly renaming = signal<string | null>(null);
  private readonly renameBox = viewChild<ElementRef<HTMLInputElement>>('renameBox');

  // The palette: the smart boxes of the drawing's cloud in the catalog's groups, the pinned services in the group their
  // type belongs to, and the pinned services of no known group last, in OTHERS.
  protected readonly groups = computed(() => {
    const groups = new Map<string, PaletteItem[]>();
    const add = (group: string, item: PaletteItem) => groups.set(group, [...(groups.get(group) ?? []), item]);
    const platform = this.store.sketch().settings.platform ?? 'azure';
    for (const kind of (this.store.catalog()?.kinds ?? []).filter(item => item.platform === platform)) {
      add(kind.group, { kind: kind.kind, short: kind.short, label: kind.label, description: kind.description, pinned: null });
    }

    for (const type of this.store.preferences().pins) {
      const summary = summarize(type);
      add(groupOf(type, platform), { kind: GENERIC_PREFIX + type, short: summary.short, label: summary.title, description: type, pinned: type });
    }

    const others = groups.get(OTHERS);
    groups.delete(OTHERS);
    if (others) {
      groups.set(OTHERS, others);
    }

    return [...groups.entries()].map(([name, items]) => ({ name, items }));
  });

  protected readonly codeLabel = computed(() => {
    const output = this.store.setting('output');
    return output === 'bicep' ? 'Bicep' : output === 'both' ? 'Code' : 'Terraform';
  });

  constructor() {
    effect(() => this.renameBox()?.nativeElement.focus());

    // Approving a proposal (a description, an import or a recommendation) shows the code it produced.
    effect(() => {
      if (this.store.approvals() > 0) {
        this.tab.set('code');
      }
    });
  }

  @HostListener('document:keydown', ['$event'])
  protected onKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (event.key === 'Escape' && this.dialog()) {
      this.dialog.set(null);
    } else if ((event.key === 'Delete' || event.key === 'Backspace') && !this.store.previewing()
      && !['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) {
      this.store.removeSelected();
    }
  }

  // Enter or leaving the box keeps the new name; Escape keeps the old one.
  protected rename(group: string, event: Event): void {
    if (this.renaming() !== group) {
      return;
    }

    this.renaming.set(null);
    this.store.renameSection(group, (event.target as HTMLInputElement).value);
  }

  protected openDialog(dialog: 'describe' | 'import' | 'search'): void {
    this.store.proposalError.set(null);
    this.dialog.set(dialog);
  }

  protected newDrawing(): void {
    if (this.store.sketch().nodes.length === 0 || confirm('Start a new drawing? The current one is replaced.')) {
      this.store.clear();
    }
  }

  protected async example(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const name = select.value as ExampleName | '';
    select.value = '';
    if (name && (this.store.sketch().nodes.length === 0 || confirm('Open the example? The current drawing is replaced.'))) {
      await this.store.loadExample(name);
    }
  }

  protected async open(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }

    try {
      await this.store.openDrawing(file);
    } catch {
      this.notice.set('That file is not a Terraform Generator drawing.');
    }
  }

  protected async check(): Promise<void> {
    this.tab.set('code');
    await this.store.runCheck();
  }

  protected async download(): Promise<void> {
    try {
      await this.store.downloadZip();
    } catch {
      this.notice.set('The ZIP could not be created. Fix the errors first.');
    }
  }
}
