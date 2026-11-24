import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { SketchMessage } from './models';
import { SketchStore } from './sketch.store';

const FILE_ORDER = ['README.md', 'deploy.ps1', 'main.tf', 'variables.tf', 'network.tf', 'apps.tf', 'data.tf', 'security.tf', 'edge.tf', 'monitoring.tf', 'outputs.tf', 'versions.tf', 'bicep/main.bicep', 'bicep/main.bicepparam', 'deploy-bicep.ps1'];

@Component({
  selector: 'app-output',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="messages" aria-label="Checks">
      @if (store.serverError(); as error) {
        <p class="message error">{{ error }}</p>
      }
      @for (message of store.messages(); track $index) {
        <button type="button" class="message" [class]="message.level" (click)="select(message)">
          <span class="badge">{{ badge(message) }}</span>{{ message.text }}
        </button>
      }
      @if (store.check(); as check) {
        <div class="check" [class.passed]="check.passed">
          <strong>{{ check.passed ? 'The code is valid (Terraform validate and fmt, Bicep build)' : 'The check found a problem' }}</strong>
          @for (step of check.steps; track step.command) {
            @if (!check.passed || step.exitCode !== 0) {
              <pre>{{ step.command }}&#10;{{ step.output }}</pre>
            }
          }
        </div>
      }
    </section>

    <nav class="files" aria-label="Files">
      @for (name of files(); track name) {
        <button type="button" [class.active]="name === activeFile()" (click)="chosen.set(name)">{{ name }}</button>
      }
    </nav>
    @if (activeFile(); as name) {
      <div class="code-tools">
        <button type="button" class="link" (click)="copy(name)">{{ copied() === name ? 'Copied' : 'Copy' }}</button>
      </div>
      <pre class="code">{{ store.result()?.files?.[name] }}</pre>
    } @else if (store.errorCount() > 0) {
      <p class="hint pad">Fix the errors above to see the code.</p>
    } @else {
      <p class="hint pad">The code appears here as you draw.</p>
    }
  `,
  styleUrl: './panel.css',
})
export class OutputComponent {
  protected readonly store = inject(SketchStore);
  protected readonly chosen = signal('README.md');
  protected readonly copied = signal<string | null>(null);

  protected readonly files = computed(() => {
    const names = Object.keys(this.store.result()?.files ?? {});
    return names.sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  });

  protected readonly activeFile = computed(() => {
    const files = this.files();
    return files.includes(this.chosen()) ? this.chosen() : files[0] ?? null;
  });

  protected badge(message: SketchMessage): string {
    return message.level === 'error' ? 'Error' : message.level === 'warning' ? 'Warning' : 'Note';
  }

  protected select(message: SketchMessage): void {
    if (message.nodeId && this.store.sketch().nodes.some(node => node.id === message.nodeId)) {
      this.store.selection.set({ type: 'node', id: message.nodeId });
    } else if (message.edgeId) {
      this.store.selection.set({ type: 'edge', id: message.edgeId });
    }
  }

  protected async copy(name: string): Promise<void> {
    await navigator.clipboard.writeText(this.store.result()?.files?.[name] ?? '');
    this.copied.set(name);
    setTimeout(() => this.copied.set(null), 1500);
  }
}

function rank(name: string): number {
  const index = FILE_ORDER.indexOf(name);
  return index < 0 ? FILE_ORDER.length : index;
}
