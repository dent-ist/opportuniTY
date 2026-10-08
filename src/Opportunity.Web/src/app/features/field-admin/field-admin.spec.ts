import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Injectable, Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FakeApi as FakeSessionApi, provideFakeApi } from '../../core/api/fake-api.testing';
import type {
  CodingLayoutRequest,
  CodingLayoutResource,
  CreateFieldRequest,
  FieldResource,
  UpdateFieldRequest,
} from '../../core/api/generated/models';
import {
  AdminField,
  AdminLayout,
  CapacityEntry,
  FieldAdminApi,
} from '../../core/fields/field-admin-api';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { DialogService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { CodingLayoutsPage } from './coding-layouts-page';
import { draftOf, previewOf, requestOf, validateDraft } from './field-admin-model';
import { FieldsPage } from './fields-page';

const CAPS = {
  sortable: false,
  filterable: true,
  rangeable: false,
  aggregatable: true,
  fullText: false,
  wildcard: false,
  leadingWildcard: false,
  highlightable: false,
  exists: true,
};

function resource(
  fieldId: number,
  displayName: string,
  type: FieldResource['type'],
  extra: Partial<FieldResource> = {},
): FieldResource {
  return {
    fieldId,
    displayName,
    queryName: displayName.toLowerCase().replace(/\W+/g, '_'),
    type,
    storage: 'coding',
    multiValue: type === 'multiChoice',
    isSystem: false,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    capabilities: CAPS,
    reducedCapabilities: false,
    choices: null,
    ...extra,
  };
}

const FIELDS: FieldResource[] = [
  resource(1, 'Control Number', 'keyword', {
    storage: 'column',
    isSystem: true,
    capabilities: { ...CAPS, sortable: true },
  }),
  resource(1000, 'Privilege', 'singleChoice', {
    isSecurityAffecting: true,
    choices: [
      { choiceId: 1, name: 'Withhold', isActive: true, systemKey: 'privilege-status.withhold' },
      { choiceId: 2, name: 'Produce', isActive: true },
    ],
  }),
  resource(1001, 'Privilege Basis', 'multiChoice', {
    choices: [{ choiceId: 11, name: 'Attorney-Client', isActive: true }],
  }),
  resource(1002, 'Key Document', 'boolean'),
  resource(1003, 'Notes', 'text'),
];

function admin(field: FieldResource, extra: Partial<AdminField> = {}): AdminField {
  return {
    ...field,
    fieldId: Number(field.fieldId),
    description: null,
    securityClass: field.isSecurityAffecting ? 'privilegeStatus' : null,
    decimalPrecision: null,
    decimalScale: null,
    textAnalysis: null,
    isSearchable: true,
    limitations: ['This field will not be sortable.'],
    hasValues: false,
    version: 4,
    choices:
      field.choices?.map((c) => ({
        choiceId: Number(c.choiceId),
        name: c.name,
        isActive: c.isActive,
        inUse: c.choiceId === 1,
        isBuiltIn: !!c.systemKey,
      })) ?? null,
    ...extra,
  };
}

const LAYOUT: AdminLayout = {
  layoutId: 'layout-1',
  name: 'Privilege Review',
  isDefault: false,
  roles: ['PrivilegeReviewer'],
  version: 2,
  sections: [
    {
      sectionId: 's-1',
      title: 'Privilege',
      fields: [
        { fieldId: 1000, isRequired: true, isReadOnly: false, visibleWhen: null },
        {
          fieldId: 1001,
          isRequired: true,
          isReadOnly: false,
          visibleWhen: { fieldId: 1000, choiceIds: [1], booleanValue: null },
        },
      ],
    },
    {
      sectionId: 's-2',
      title: 'Notes',
      fields: [{ fieldId: 1003, isRequired: false, isReadOnly: false, visibleWhen: null }],
    },
  ],
};

@Injectable()
class FakeAdminApi extends FieldAdminApi {
  created: CreateFieldRequest[] = [];
  updated: { field: AdminField; request: UpdateFieldRequest }[] = [];
  layoutSaves: { layout: AdminLayout | null; request: CodingLayoutRequest }[] = [];
  reorders: number[][] = [];
  reject: HttpErrorResponse | null = null;
  current: AdminField = admin(FIELDS[1]);

  async fields() {
    return FIELDS;
  }
  async capacity(): Promise<CapacityEntry[]> {
    return [
      {
        storage: 'coding',
        type: 'singleChoice',
        textAnalysis: null,
        budget: 100,
        available: 99,
        capabilities: CAPS,
        limitations: ['This field will not be sortable.'],
      },
      {
        storage: 'coding',
        type: 'integer',
        textAnalysis: null,
        budget: 10,
        available: 0,
        capabilities: { ...CAPS, aggregatable: false },
        limitations: ['This workspace has used all search slots for this type.'],
      },
    ];
  }
  async field(fieldId: number) {
    const found = FIELDS.find((f) => Number(f.fieldId) === fieldId)!;
    return fieldId === 1000 ? this.current : admin(found);
  }
  async createField(request: CreateFieldRequest) {
    if (this.reject) throw this.reject;
    this.created.push(request);
    return admin(resource(2000, request.displayName, request.type), { version: 1, choices: [] });
  }
  async updateField(field: AdminField, request: UpdateFieldRequest) {
    if (this.reject) throw this.reject;
    this.updated.push({ field, request });
    return { ...field, displayName: request.displayName, version: field.version + 1 };
  }
  async retireField() {}
  async addChoice(field: AdminField) {
    return field;
  }
  async updateChoice(field: AdminField) {
    return field;
  }
  async deleteChoice(field: AdminField) {
    return field;
  }
  async reorderChoices(field: AdminField, ids: readonly number[]) {
    this.reorders.push([...ids]);
    const choices = ids.map((id) => field.choices!.find((c) => c.choiceId === id)!);
    this.current = { ...field, choices, version: field.version + 1 };
    return this.current;
  }
  async layouts(): Promise<CodingLayoutResource[]> {
    return [
      { layoutId: 'layout-1', name: LAYOUT.name, isDefault: false, sections: LAYOUT.sections },
    ];
  }
  async layout() {
    return LAYOUT;
  }
  async createLayout(request: CodingLayoutRequest) {
    this.layoutSaves.push({ layout: null, request });
    return { ...LAYOUT, layoutId: 'layout-2', name: request.name, version: 1 };
  }
  async updateLayout(layout: AdminLayout, request: CodingLayoutRequest) {
    if (this.reject) throw this.reject;
    this.layoutSaves.push({ layout, request });
    return {
      ...layout,
      ...request,
      roles: request.roles ?? [],
      version: layout.version + 1,
    } as AdminLayout;
  }
  async deleteLayout() {}
}

describe('Admin › Fields, Choices and Coding Layouts (E04-T06)', () => {
  let api: FakeAdminApi;
  let root: HTMLElement;

  async function mount<T>(component: Type<T>): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideFakeApi(new FakeSessionApi()),
        { provide: DialogService, useValue: { confirm: async () => true } },
        {
          provide: WorkspaceContext,
          useValue: {
            workspaceId: 'ws-1',
            can: () => true,
            apiUrl: (...s: string[]) => s.join('/'),
          },
        },
      ],
    });
    TestBed.overrideComponent(component, {
      set: { providers: [{ provide: FieldAdminApi, useClass: FakeAdminApi }] },
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    const fixture = TestBed.createComponent(component);
    api = fixture.debugElement.injector.get(FieldAdminApi) as FakeAdminApi;
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      TestBed.tick();
    }
  }

  const button = (name: string) =>
    [...root.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name,
    );
  const type = (el: HTMLInputElement, value: string) => {
    el.value = value;
    el.dispatchEvent(new Event('input'));
  };
  const select = (label: string) =>
    [...root.querySelectorAll<HTMLSelectElement>('select')].find(
      (s) => root.querySelector(`label[for="${s.id}"]`)?.textContent?.trim() === label,
    )!;

  afterEach(() => root?.remove());

  describe('Fields', () => {
    it('lists fields with type, source and search, and shows limitations before creating', async () => {
      await mount(FieldsPage);
      const rows = [...root.querySelectorAll('tbody tr')].map((r) => r.textContent ?? '');
      expect(rows.find((r) => r.includes('Privilege Basis'))).toContain('Multiple Choice');
      expect(rows.find((r) => r.includes('Control Number'))).toContain('System');
      expect(rows.filter((r) => r.includes('Affects access'))).toEqual([
        expect.stringContaining('Single Choice'),
      ]);
      await expectNoAxeViolations(root);

      button('New field')!.click();
      await settle();
      expect(root.textContent).toContain('This field will not be sortable.');
      const typeSelect = select('Type');
      typeSelect.value = 'integer';
      typeSelect.dispatchEvent(new Event('change'));
      await settle();
      expect(root.textContent).toContain('used all search slots');

      type(root.querySelector<HTMLInputElement>('opp-text-field input')!, 'Pages Reviewed');
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      expect(api.created[0]).toEqual(
        expect.objectContaining({
          displayName: 'Pages Reviewed',
          type: 'integer',
          storage: 'coding',
        }),
      );
    });

    it('locks the type of a field with values and explains why', async () => {
      await mount(FieldsPage);
      api.current = admin(FIELDS[1], { hasValues: true });
      button('Edit Privilege')!.click();
      await settle();
      expect(select('Type').disabled).toBe(true);
      expect(root.textContent).toContain('Documents already hold values for this field');
      // Used choices can only be deactivated: no delete button for Withhold, one for Produce.
      expect(button('Delete Withhold')).toBeUndefined();
      expect(button('Delete Produce')).toBeDefined();
      await expectNoAxeViolations(root);
    });

    it('marks built-in choices and offers neither Deactivate nor Delete for them (E13-T01)', async () => {
      await mount(FieldsPage);
      button('Edit Privilege')!.click();
      await settle();
      const rows = [...root.querySelectorAll<HTMLElement>('.fa__choice')];
      expect(rows[0].textContent).toContain('Built-in');
      expect(rows[1].textContent).not.toContain('Built-in');
      expect(button('Deactivate Withhold')).toBeUndefined();
      expect(button('Deactivate Produce')).toBeDefined();
      expect(button('Rename Withhold')).toBeDefined();
      await expectNoAxeViolations(root);
    });

    it('reorders choices with Move up and keeps focus on the moved choice', async () => {
      await mount(FieldsPage);
      button('Edit Privilege')!.click();
      await settle();
      button('Move Produce up')!.click();
      await settle();
      expect(api.reorders).toEqual([[2, 1]]);
      expect(document.activeElement?.getAttribute('aria-label')).toBe('Move Produce down');
    });

    it('shows a 412 as a change by someone else', async () => {
      await mount(FieldsPage);
      button('Edit Notes')!.click();
      await settle();
      api.reject = new HttpErrorResponse({
        status: 412,
        error: { status: 412, title: 'Conflict' },
      });
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      expect(root.querySelector('[role="alert"]')!.textContent).toContain('Someone else changed');
    });
  });

  describe('Coding Layouts', () => {
    it('moves fields and sections with buttons, previews the coding pane and saves with the version', async () => {
      await mount(CodingLayoutsPage);
      button('Privilege Review')?.click();
      [...root.querySelectorAll<HTMLButtonElement>('.fa__layout-item')][0].click();
      await settle();
      const preview = () => root.querySelector('.fa__preview')!;
      const titles = () =>
        [...preview().querySelectorAll('h3')].map((h) => h.textContent?.trim() ?? '');
      expect(titles()).toEqual(['Privilege', 'Notes']);
      // The conditional Privilege Basis shows only while Privilege is Withhold, as in Review mode.
      expect(preview().textContent).not.toContain('Privilege Basis');

      button('Move section Notes up')!.click();
      await settle();
      expect(titles()).toEqual(['Notes', 'Privilege']);

      button('Move Privilege down')!.click();
      await settle();
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      const saved = api.layoutSaves[0];
      expect(saved.layout?.version).toBe(2);
      expect(saved.request.sections.map((s) => s.title)).toEqual(['Notes', 'Privilege']);
      expect(saved.request.sections[1].fields.map((f) => f.fieldId)).toEqual([1001, 1000]);
      expect(saved.request.roles).toEqual(['PrivilegeReviewer']);
      await expectNoAxeViolations(root);
    });

    it('never offers Apply to Family for a security-affecting field', async () => {
      await mount(CodingLayoutsPage);
      [...root.querySelectorAll<HTMLButtonElement>('.fa__layout-item')][0].click();
      await settle();
      const row = root.querySelector('[data-layout-field="1000"]')!;
      const family = [...row.querySelectorAll('opp-checkbox')].find((c) =>
        c.textContent?.includes('Apply to Family'),
      )!;
      expect(family.querySelector('input')!.disabled).toBe(true);
      expect(row.textContent).toContain('never pre-selected for the family');
    });
  });

  describe('layout drafts', () => {
    const fields = new Map(FIELDS.map((f) => [Number(f.fieldId), f]));

    it('round-trips a layout and builds the coding pane layout from it', () => {
      const draft = draftOf(LAYOUT);
      expect(requestOf(draft).sections[0].fields[1].visibleWhen).toEqual({
        fieldId: 1000,
        choiceIds: [1],
        booleanValue: null,
      });
      const preview = previewOf(draft, FIELDS);
      expect(preview.sections.map((s) => s.title)).toEqual(['Privilege', 'Notes']);
      expect(preview.sections[0].fields[1].visibleWhen).toEqual({
        queryName: 'privilege',
        choices: ['Withhold'],
        value: null,
      });
    });

    it('reports conditions on fields outside the layout and required read-only fields', () => {
      const draft = draftOf(LAYOUT);
      const broken = {
        ...draft,
        name: ' ',
        sections: [
          {
            ...draft.sections[0],
            fields: [
              { ...draft.sections[0].fields[1] },
              {
                fieldId: 1003,
                required: true,
                readOnly: true,
                applyToFamily: false,
                condition: null,
              },
            ],
          },
        ],
      };
      expect(validateDraft(broken, fields)).toEqual({
        name: 'Enter a layout name.',
        f1001: 'The field it depends on must be in this layout.',
        f1003: 'Only editable coding fields can be required.',
      });
    });
  });
});
