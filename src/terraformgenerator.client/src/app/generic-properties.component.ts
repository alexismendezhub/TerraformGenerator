import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { EXTRA_HCL, REFERENCE_PREFIX, ResourceSchema, SchemaAttribute, SketchNode, isGeneric, typeOf } from './models';
import { SketchStore } from './sketch.store';

interface Field {
  key: string;
  label: string;
  attribute: SchemaAttribute;
}

// Fields Terraform Generator fills in by itself when left empty, by cloud (the same as GenericGenerator.AutoFields).
const AUTO: Record<string, string[]> = {
  azure: ['resource_group_name', 'location', 'tags'],
  aws: ['tags', 'tags_all', 'region'],
  gcp: ['project', 'region', 'labels'],
};

// The fields of a box for any provider resource: required first, then the rest with a filter; a field can hold a
// value, an expression (=...) or point to another box.
@Component({
  selector: 'app-generic-properties',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2>{{ store.labelOf(node().kind) }}</h2>
    <p class="hint"><code>{{ type() }}</code> ·
      <a [href]="docs()" target="_blank" rel="noopener">What each field means (Terraform docs)</a></p>

    <label class="field">
      <span>Name</span>
      <input type="text" autocomplete="off" spellcheck="false" [value]="node().name" (input)="store.renameNode(node().id, value($event))" />
      <small>Lowercase letters, digits and hyphens. The resource in Terraform is named after it.</small>
    </label>

    @if (!schema()) {
      <p class="hint">{{ loading() ? 'Loading the fields of ' + type() + '…' : 'The fields of this resource could not be loaded.' }}</p>
    } @else {
      @if (auto().length > 0) {
        <p class="hint small">Filled in for you when left empty: {{ auto().join(', ') }}.</p>
      }

      @if (required().length > 0) {
        <h3>Required</h3>
        @for (field of required(); track field.key) {
          <ng-container [ngTemplateOutlet]="fieldTemplate" [ngTemplateOutletContext]="{ $implicit: field }" />
        }
      }

      <h3>Other fields ({{ optional().length }})</h3>
      <input type="text" class="filter" autocomplete="off" spellcheck="false" placeholder="Filter fields, for example tags or version"
             [value]="filter()" (input)="filter.set(value($event))" />
      @for (field of shownOptional(); track field.key) {
        <ng-container [ngTemplateOutlet]="fieldTemplate" [ngTemplateOutletContext]="{ $implicit: field }" />
      }
      @if (hiddenCount() > 0) {
        <p class="hint small">{{ hiddenCount() }} more: type in the filter to find them.</p>
      }

      <h3>Extra HCL</h3>
      <textarea rows="5" autocomplete="off" spellcheck="false" placeholder="Anything the fields above cannot express, written in HCL, for example a nested block."
                [value]="current(extraHcl)" (input)="setOrClear(extraHcl, value($event))"></textarea>
    }

    @if (schema() && store.aiStatus()?.enabled) {
      <h3>Fill with AI</h3>
      <textarea rows="2" autocomplete="off" placeholder="Optional: what it is for, for example keep files 90 days and encrypt them."
                [value]="purpose()" (input)="purpose.set(value($event))"></textarea>
      <button type="button" [disabled]="store.proposing() || store.previewing()" (click)="fill()">
        {{ store.proposing() ? 'Asking the AI…' : 'Fill the fields with AI' }}</button>
      <p class="hint small">The AI proposes the values; you review them on the drawing and approve or discard them.</p>
    }

    <button type="button" class="danger" (click)="store.removeSelected()">Delete {{ node().name }}</button>

    <ng-template #fieldTemplate let-field>
      <div class="field generic">
        <span>{{ field.label }} <small class="type">{{ field.attribute.type }}</small></span>
        @if (reference(field.key); as ref) {
          <div class="row">
            <span class="ref">→ {{ nameOf(ref.nodeId) }}.{{ ref.attribute }}</span>
            <button type="button" class="link" (click)="store.clearProp(node().id, field.key)">Clear</button>
          </div>
        } @else {
          <div class="row">
            @if (field.attribute.type === 'bool') {
              <select autocomplete="off" (change)="setOrClear(field.key, value($event))">
                <option value="" [selected]="!current(field.key)">(not set)</option>
                <option value="true" [selected]="current(field.key) === 'true'">true</option>
                <option value="false" [selected]="current(field.key) === 'false'">false</option>
              </select>
            } @else {
              <input type="text" autocomplete="off" spellcheck="false" [value]="current(field.key)" [placeholder]="placeholder(field.attribute)"
                     (change)="setOrClear(field.key, value($event))" />
            }
            @if (others().length > 0) {
              <select class="pick" autocomplete="off" title="Or take the value from another box" (change)="pick(field.key, $event)">
                <option value="">or a box…</option>
                @for (other of others(); track other.id) {
                  <option [value]="other.id">{{ other.name }}</option>
                }
              </select>
            }
          </div>
        }
        @if (field.attribute.description) { <small>{{ field.attribute.description }}</small> }
      </div>
    </ng-template>
  `,
  styleUrl: './panel.css',
  imports: [NgTemplateOutlet],
})
export class GenericPropertiesComponent {
  protected readonly store = inject(SketchStore);
  readonly node = input.required<SketchNode>();
  protected readonly extraHcl = EXTRA_HCL;
  protected readonly filter = signal('');
  protected readonly purpose = signal('');
  protected readonly loading = signal(false);
  private readonly loaded = signal<ResourceSchema | null>(null);

  protected readonly type = computed(() => typeOf(this.node().kind));

  // The AI fills this box's fields through a normal proposal; every other box stays as it is.
  protected async fill(): Promise<void> {
    const node = this.node();
    const required = this.required().map(field => field.key).join(', ');
    const purpose = this.purpose().trim();
    const description = `Fill in the fields of the box "${node.name}" (id ${node.id}, ${this.type()}). ` +
      (required ? `Its required fields are ${required}. ` : '') +
      'Set the fields that make it work and make it safe, point fields at other boxes with ref: where they belong, and keep every other box, id and arrow exactly as it is.' +
      (purpose ? `
What it is for: ${purpose}` : '');
    if (await this.store.propose(description, false)) {
      this.purpose.set('');
    }
  }
  protected readonly schema = computed(() => this.store.schemas()[this.type()] ?? this.loaded());

  constructor() {
    effect(() => {
      const type = this.type();
      if (!this.store.schemas()[type]) {
        this.loading.set(true);
        void this.store.loadSchema(type).then(schema => {
          this.loaded.set(schema);
          this.loading.set(false);
        });
      }
    });
  }

  protected readonly docs = computed(() => {
    const type = this.type();
    const provider = type.startsWith('aws_') ? 'aws' : type.startsWith('google_') ? 'google' : 'azurerm';
    return `https://registry.terraform.io/providers/hashicorp/${provider}/latest/docs/resources/${type.slice(type.indexOf('_') + 1)}`;
  });

  protected readonly auto = computed(() => {
    const names = new Set((this.schema()?.attributes ?? []).map(attribute => attribute.name));
    const list = (AUTO[this.store.sketch().settings.platform ?? 'azure'] ?? []).filter(name => names.has(name) && !this.node().props[name]);
    const nameAttribute = this.schema()?.attributes.find(attribute => attribute.name === 'name');
    return nameAttribute?.required && !this.node().props['name'] ? ['name', ...list] : list;
  });

  private readonly fields = computed<Field[]>(() => {
    const schema = this.schema();
    if (!schema) {
      return [];
    }

    const hidden = new Set(['id', 'tags_all']);
    const top = schema.attributes.filter(attribute => (attribute.required || attribute.optional) && !hidden.has(attribute.name))
      .map(attribute => ({ key: attribute.name, label: attribute.name, attribute }));
    const nested = schema.blocks.flatMap(block => block.attributes.filter(attribute => attribute.required || attribute.optional)
      .map(attribute => ({ key: `${block.name}.${attribute.name}`, label: `${block.name} › ${attribute.name}`, attribute: { ...attribute, required: block.min > 0 && attribute.required } })));
    return [...top, ...nested];
  });

  protected readonly required = computed(() => this.fields().filter(field => field.attribute.required && !this.auto().includes(field.key)));
  protected readonly optional = computed(() => this.fields().filter(field => !field.attribute.required || this.auto().includes(field.key)));

  // Fields with a value, then the ones matching the filter (at most 25).
  protected readonly shownOptional = computed(() => {
    const filter = this.filter().trim().toLowerCase();
    const props = this.node().props;
    const withValue = this.optional().filter(field => props[field.key]);
    const matching = filter.length === 0 ? [] : this.optional().filter(field => !props[field.key] && field.key.toLowerCase().includes(filter)).slice(0, 25);
    return [...withValue, ...matching];
  });

  protected readonly hiddenCount = computed(() => this.optional().length - this.shownOptional().length);

  protected readonly others = computed(() => this.store.sketch().nodes.filter(other => other.id !== this.node().id));

  protected current(key: string): string {
    return this.node().props[key] ?? '';
  }

  protected reference(key: string): { nodeId: string; attribute: string } | null {
    const value = this.node().props[key];
    if (!value?.startsWith(REFERENCE_PREFIX)) {
      return null;
    }

    const target = value.slice(REFERENCE_PREFIX.length);
    const dot = target.lastIndexOf('.');
    return { nodeId: target.slice(0, dot), attribute: target.slice(dot + 1) };
  }

  protected nameOf(id: string): string {
    return this.store.sketch().nodes.find(node => node.id === id)?.name ?? '?';
  }

  protected placeholder(attribute: SchemaAttribute): string {
    if (attribute.type.startsWith('list(') || attribute.type.startsWith('set(')) {
      return 'Values separated by commas';
    }

    if (attribute.type.startsWith('map(')) {
      return 'key=value, key2=value2';
    }

    return attribute.type === 'number' ? 'A number' : attribute.type === 'string' ? '' : 'An HCL expression, starting with =';
  }

  protected setOrClear(key: string, value: string): void {
    if (value.trim().length === 0) {
      this.store.clearProp(this.node().id, key);
    } else {
      this.store.setProp(this.node().id, key, value);
    }
  }

  // Pointing a field to another box: provider boxes give their id, catalog boxes their main resource's id.
  protected pick(key: string, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const target = select.value;
    select.value = '';
    if (!target) {
      return;
    }

    const other = this.store.sketch().nodes.find(node => node.id === target);
    const attribute = key.endsWith('_arn') || key.endsWith('arn') ? 'arn' : key.endsWith('_name') || key === 'name' ? 'name' : 'id';
    this.store.setReference(this.node().id, key, target, other && isGeneric(other.kind) ? attribute : attribute === 'arn' ? 'id' : attribute);
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
