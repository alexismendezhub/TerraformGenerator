import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { GenericPropertiesComponent } from './generic-properties.component';
import { PropertyDefinition, REFERENCE_PREFIX, SketchNode, isGeneric } from './models';
import { SketchStore } from './sketch.store';

@Component({
  selector: 'app-properties',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [GenericPropertiesComponent],
  template: `
    @if (store.selectedNode(); as node) {
      @if (isGeneric(node.kind)) {
        <app-generic-properties [node]="node" />
      } @else if (store.kind(node.kind); as kind) {
        <h2>{{ kind.label }}</h2>
        <p class="hint">{{ kind.description }}</p>

        <label class="field">
          <span>Name</span>
          <input type="text" autocomplete="off" spellcheck="false" [value]="node.name"
                 [class.invalid]="!matches(node.name, namePattern())"
                 (input)="store.renameNode(node.id, value($event))" />
          <small>Lowercase letters, digits and hyphens.</small>
        </label>

        @if (environments().length > 1) {
          <label class="field">
            <span>Values for</span>
            <select autocomplete="off" (change)="valuesFor.set(value($event))">
              <option value="" [selected]="valuesFor() === ''">All environments</option>
              @for (environment of environments(); track environment) {
                <option [value]="environment" [selected]="valuesFor() === environment">{{ environment }} only</option>
              }
            </select>
            <small>{{ valuesFor() ? 'A choice changed here applies only to ' + valuesFor() + '.' : 'Choose an environment to give it its own values.' }}</small>
          </label>
        }

        @for (property of kind.properties; track property.key) {
          @switch (property.type) {
            @case ('select') {
              <label class="field">
                <span>{{ property.label }}</span>
                <select autocomplete="off" [value]="prop(node, property)" (change)="set(node, property.key, value($event))">
                  @for (option of property.options; track option.value) {
                    <option [value]="option.value" [selected]="option.value === prop(node, property)">{{ option.label }}</option>
                  }
                </select>
                @if (property.help) { <small>{{ property.help }}</small> }
              </label>
            }
            @case ('toggle') {
              <label class="toggle">
                <input type="checkbox" autocomplete="off" [checked]="prop(node, property) === 'true'"
                       (change)="set(node, property.key, checked($event))" />
                <span>{{ property.label }}</span>
              </label>
              @if (property.help) { <small class="toggle-help">{{ property.help }}</small> }
            }
            @default {
              @if (visible(node, property)) {
                <label class="field">
                  <span>{{ property.label }}</span>
                  <input type="text" autocomplete="off" spellcheck="false" [value]="prop(node, property)"
                         [class.invalid]="!matches(prop(node, property), property.pattern)"
                         (input)="set(node, property.key, value($event))" />
                  @if (property.help) { <small>{{ property.help }}</small> }
                </label>
              }
            }
          }
        }

        @if (overrides(node).length > 0) {
          <h3>Different by environment</h3>
          <ul class="connections">
            @for (override of overrides(node); track override.key) {
              <li>{{ override.text }}
                <button type="button" class="link" (click)="store.clearProp(node.id, override.key)">Use the same as all</button></li>
            }
          </ul>
        }

        @if (connections().length > 0) {
          <h3>Connections</h3>
          <ul class="connections">
            @for (connection of connections(); track connection.id) {
              <li><button type="button" class="link" (click)="store.selection.set({ type: 'edge', id: connection.id })">{{ connection.text }}</button></li>
            }
          </ul>
        }

        <button type="button" class="danger" (click)="store.removeSelected()">Delete {{ node.name }}</button>
      }
    } @else if (store.selectedEdge(); as edge) {
      <h2>Connection</h2>
      <p><strong>{{ nameOf(edge.from) }}</strong> → <strong>{{ nameOf(edge.to) }}</strong></p>
      @if (fromGeneric(edge.from); as from) {
        <p class="hint">An arrow from a box of the full list means one of its fields holds the other box.</p>
        <label class="field">
          <span>Which field of {{ from.name }} points to {{ nameOf(edge.to) }}?</span>
          <select autocomplete="off" (change)="linkField(from, edge.to, $event)">
            <option value="">{{ linkedField(from, edge.to) ?? 'Choose a field…' }}</option>
            @for (name of fieldNames(from); track name) {
              <option [value]="name">{{ name }}</option>
            }
          </select>
        </label>
      } @else {
        <p class="hint">{{ store.connectionText(edge) }}</p>
      }
      <button type="button" class="danger" (click)="store.removeSelected()">Delete connection</button>
    } @else {
      @if (store.catalog(); as catalog) {
        <h2>Project</h2>
        <label class="field">
          <span>Cloud</span>
          <select autocomplete="off" (change)="changePlatform($event)">
            @for (platform of store.platforms(); track platform.id) {
              <option [value]="platform.id" [selected]="platform.id === store.sketch().settings.platform">{{ platform.label }}</option>
            }
          </select>
          <small>Changing it starts a new drawing: every cloud has its own services.</small>
        </label>
        <label class="field">
          <span>Project name</span>
          <input type="text" autocomplete="off" spellcheck="false" [value]="store.sketch().settings.name"
                 [class.invalid]="!matches(store.sketch().settings.name, catalog.projectNamePattern)"
                 (input)="store.setSettings({ name: value($event) })" />
          <small>2 to 12 lowercase letters or digits. Every resource name starts with it.</small>
        </label>
        <label class="field">
          <span>Environment</span>
          <select autocomplete="off" (change)="store.setSettings({ environment: value($event) })">
            @for (option of catalog.environments; track option.value) {
              <option [value]="option.value" [selected]="option.value === store.sketch().settings.environment">{{ option.label }}</option>
            }
          </select>
          <small>Production adds safer defaults and warnings.</small>
        </label>
        <label class="field">
          <span>Region</span>
          <select autocomplete="off" (change)="store.setSettings({ region: value($event) })">
            @for (option of store.platform()?.regions ?? catalog.regions; track option.value) {
              <option [value]="option.value" [selected]="option.value === store.sketch().settings.region">{{ option.label }}</option>
            }
          </select>
        </label>
        <label class="toggle">
          <input type="checkbox" autocomplete="off" [checked]="store.sketch().settings.monitoring"
                 (change)="store.setSettings({ monitoring: checkedBool($event) })" />
          <span>{{ store.sketch().settings.platform === 'azure' ? 'Monitoring (logs, metrics and Application Insights)' : 'Monitoring (traces of every request and a dashboard)' }}</span>
        </label>

        @for (group of settingGroups(); track group.name) {
          <details class="section" [open]="group.name === 'Account and tags' || group.name === 'Code and deployment'">
            <summary>{{ group.name }}</summary>
            @for (setting of group.settings; track setting.key) {
              @if (showSetting(setting)) {
                @if (setting.type === 'toggle') {
                  <label class="toggle">
                    <input type="checkbox" autocomplete="off" [checked]="store.setting(setting.key) === 'true'"
                           (change)="store.setOption(setting.key, checked($event))" />
                    <span>{{ setting.label }}</span>
                  </label>
                  @if (setting.help) { <small class="toggle-help">{{ setting.help }}</small> }
                } @else {
                  <label class="field">
                    <span>{{ setting.label }}</span>
                    @if (setting.type === 'select') {
                      <select autocomplete="off" (change)="store.setOption(setting.key, value($event))">
                        @for (option of setting.options; track option.value) {
                          <option [value]="option.value" [selected]="option.value === store.setting(setting.key)">{{ option.label }}</option>
                        }
                      </select>
                    } @else {
                      <input type="text" autocomplete="off" spellcheck="false" [value]="store.setting(setting.key)" [placeholder]="placeholder(setting.key)"
                             [class.invalid]="!matches(store.setting(setting.key), setting.pattern)"
                             (input)="store.setOption(setting.key, value($event))" />
                    }
                    @if (setting.help) { <small>{{ setting.help }}</small> }
                  </label>
                }
              }
            }
          </details>
        }

        <h3>How it works</h3>
        <ol class="hint steps">
          <li>Add resources from the left, or click <strong>Describe with AI</strong> and say what you need in your words, or import Terraform you already have.</li>
          <li>Drag from the dot on a box's right edge to another box to connect them (for example an app to its database).</li>
          <li>Click a box to choose its options here. <strong>AI review</strong> suggests improvements you can apply.</li>
          <li>The code tab shows Terraform (and Bicep) as you draw. Check it, then download the ZIP and run <code>deploy.ps1</code>: it explains every step.</li>
        </ol>
      }
    }
  `,
  styleUrl: './panel.css',
})
export class PropertiesComponent {
  protected readonly store = inject(SketchStore);
  protected readonly namePattern = computed(() => this.store.catalog()?.namePattern ?? null);

  protected readonly connections = computed(() => {
    const node = this.store.selectedNode();
    if (!node) {
      return [];
    }

    return this.store.sketch().edges
      .filter(edge => edge.from === node.id || edge.to === node.id)
      .map(edge => ({ id: edge.id, text: `${this.nameOf(edge.from)} → ${this.nameOf(edge.to)}` }));
  });

  // The environments of the drawing, and the one whose values the panel shows ('' for all).
  protected readonly environments = computed(() => {
    const chosen = this.store.setting('environments');
    return !chosen || chosen === 'one' ? [] : chosen.split(',');
  });
  protected readonly valuesFor = signal('');

  private keyFor(key: string): string {
    const environment = this.environments().includes(this.valuesFor()) ? this.valuesFor() : '';
    return environment ? `${key}@${environment}` : key;
  }

  protected prop(node: SketchNode, property: PropertyDefinition): string {
    return node.props[this.keyFor(property.key)] ?? node.props[property.key] ?? property.default;
  }

  protected set(node: SketchNode, key: string, value: string): void {
    this.store.setProp(node.id, this.keyFor(key), value);
  }

  // The choices that differ in some environment, in plain words.
  protected overrides(node: SketchNode): { key: string; text: string }[] {
    const kind = this.store.kind(node.kind);
    return Object.entries(node.props).filter(([key]) => key.includes('@')).map(([key, value]) => {
      const [name, environment] = key.split('@');
      const property = kind?.properties.find(item => item.key === name);
      const label = property?.options.find(option => option.value === value)?.label ?? value;
      return { key, text: `${property?.label ?? name} in ${environment}: ${label}` };
    });
  }

  // The project settings in the catalog's sections, in order.
  protected readonly settingGroups = computed(() => {
    const groups = new Map<string, PropertyDefinition[]>();
    for (const setting of this.store.catalog()?.settings ?? []) {
      const name = setting.group ?? 'More';
      groups.set(name, [...(groups.get(name) ?? []), setting]);
    }

    // Sections with nothing that applies to this cloud are left out.
    return [...groups.entries()].map(([name, settings]) => ({ name, settings })).filter(group => group.settings.some(setting => this.showSetting(setting)));
  });

  // A field with showWhen ("key=value" or "key!=value", several joined with &) shows only when all of them hold.
  private holds(showWhen: string | null | undefined, valueOf: (key: string) => string): boolean {
    return (showWhen ?? '').split('&').filter(condition => condition.length > 0).every(condition => {
      const negate = condition.includes('!=');
      const [key, value] = condition.split(negate ? '!=' : '=');
      return (valueOf(key) === value) !== negate;
    });
  }

  protected visible(node: SketchNode, property: PropertyDefinition): boolean {
    const kind = this.store.kind(node.kind);
    return this.holds(property.showWhen, key => node.props[this.keyFor(key)] ?? node.props[key] ?? kind?.properties.find(item => item.key === key)?.default ?? '');
  }

  protected showSetting(setting: PropertyDefinition): boolean {
    return this.holds(setting.showWhen, key => this.store.settingValue(key));
  }

  protected placeholder(key: string): string {
    const settings = this.store.sketch().settings;
    return key === 'resourceGroupName' ? `${settings.name}-${settings.environment}-rg`
      : key === 'stateResourceGroup' ? `${settings.name}-tfstate-rg`
        : key === 'subscriptionId' ? 'The one az login uses' : '';
  }

  protected readonly isGeneric = isGeneric;

  protected changePlatform(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const current = this.store.sketch().settings.platform ?? 'azure';
    if (this.store.sketch().nodes.length > 0 && !confirm('Change the cloud? This starts a new drawing.')) {
      select.value = current;
      return;
    }

    this.store.setPlatform(select.value);
  }

  protected fromGeneric(id: string): SketchNode | null {
    const node = this.store.sketch().nodes.find(item => item.id === id);
    return node && isGeneric(node.kind) ? node : null;
  }

  // The fields of a provider box that can hold another box: names ending in id, arn, name or their lists.
  protected fieldNames(node: SketchNode): string[] {
    const schema = this.store.schemas()[node.kind.slice(3)];
    if (!schema) {
      void this.store.loadSchema(node.kind.slice(3));
      return [];
    }

    const likely = (name: string) => /(_id|_ids|_arn|_arns|_name|^name)$/.test(name);
    const all = schema.attributes.filter(attribute => attribute.required || attribute.optional).map(attribute => attribute.name);
    return [...all.filter(likely), ...all.filter(name => !likely(name))];
  }

  protected linkedField(node: SketchNode, targetId: string): string | null {
    const entry = Object.entries(node.props).find(([, value]) => value.startsWith(`${REFERENCE_PREFIX}${targetId}.`));
    return entry ? `${entry[0]} (set)` : null;
  }

  protected linkField(node: SketchNode, targetId: string, event: Event): void {
    const field = (event.target as HTMLSelectElement).value;
    if (field) {
      const attribute = field.endsWith('arn') || field.endsWith('arns') ? 'arn' : field.endsWith('name') ? 'name' : 'id';
      this.store.setReference(node.id, field, targetId, attribute);
    }
  }

  protected nameOf(id: string): string {
    return this.store.sketch().nodes.find(node => node.id === id)?.name ?? '?';
  }

  protected matches(value: string, pattern: string | null | undefined): boolean {
    return !pattern || new RegExp(pattern).test(value);
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  protected checked(event: Event): string {
    return this.checkedBool(event) ? 'true' : 'false';
  }

  protected checkedBool(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }
}
