import type { Route } from '@playwright/test';

// Roles, permissions and user/group assignment (E05-T08) as the API serves them: `…/roles` (built-in roles and their
// permissions), `…/role-assignments` (one version for the whole set, as ETag), `PUT …/role-assignments/users/{id}` and
// `PUT …/role-assignments/groups?name=` with If-Match and the server's rules (self-protection, confirmation of a self
// removal, the last Workspace Admin, Break-glass), `…/role-assignments/candidates`, and the role grants of restriction
// classes (`…/security/restriction-classes`, E05-T06). The signed-in user is `user-1` (Alex Reviewer) of mock-api.ts.

type Json = Record<string, unknown>;

interface MockPrincipal {
  kind: 'user' | 'group';
  userId: string | null;
  groupName: string | null;
  displayName: string;
  email: string | null;
  roles: string[];
}

interface MockClass {
  classKey: string;
  displayName: string;
  isBuiltIn: boolean;
  roles: string[];
  rules: Json[];
  updatedAt: string;
  version: number;
}

const ROLES = [
  { key: 'WorkspaceAdmin', displayName: 'Workspace Admin' },
  { key: 'Reviewer', displayName: 'Reviewer' },
  { key: 'QcReviewer', displayName: 'QC Reviewer' },
  { key: 'PrivilegeReviewer', displayName: 'Privilege Reviewer' },
  { key: 'ProductionManager', displayName: 'Production Manager' },
  { key: 'Auditor', displayName: 'Auditor (read-only)' },
  { key: 'BreakGlass', displayName: 'Break-glass' },
];

const PERMISSIONS: [string, string, string[]][] = [
  [
    'Document.View',
    'Open a document: metadata, extracted text and renditions.',
    ROLES.map((r) => r.key),
  ],
  ['Document.DownloadNative', 'Download the native file.', ['WorkspaceAdmin', 'ProductionManager']],
  ['Search.Execute', 'Run searches and see result pages.', ROLES.map((r) => r.key)],
  [
    'Coding.Write',
    'Change coding fields that are not security-affecting.',
    ['WorkspaceAdmin', 'Reviewer', 'QcReviewer', 'PrivilegeReviewer'],
  ],
  [
    'Coding.WritePrivilege',
    'Change security-affecting fields.',
    ['WorkspaceAdmin', 'PrivilegeReviewer'],
  ],
  ['Production.Create', 'Create and run productions.', ['WorkspaceAdmin', 'ProductionManager']],
  ['Audit.Read', 'Read the workspace audit trail.', ['WorkspaceAdmin', 'Auditor', 'BreakGlass']],
  ['Workspace.ManageUsers', 'Assign and revoke workspace roles.', ['WorkspaceAdmin']],
  [
    'Workspace.ManageSecurity',
    'Manage restriction classes, class grants and ethical walls.',
    ['WorkspaceAdmin'],
  ],
];

const ME = 'user-1';
const MY_GROUPS: readonly string[] = [];
const ADMIN = 'WorkspaceAdmin';
const BREAK_GLASS = 'BreakGlass';

function problem(status: number, code: string, title: string) {
  return {
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: `urn:opportunity:problem:${code}`, title, status }),
  };
}

export class RoleAdminMock {
  version = 4;
  canAssignBreakGlass = false;
  readonly principals: MockPrincipal[] = [
    user('user-1', 'Alex Reviewer', 'alex@example.test', [ADMIN]),
    user('user-2', 'Sam Senior', 'sam@example.test', [ADMIN, 'QcReviewer']),
    user('user-3', 'Riley Reviewer', 'riley@example.test', ['Reviewer']),
    {
      kind: 'group',
      userId: null,
      groupName: 'cn=review-team',
      displayName: 'cn=review-team',
      email: null,
      roles: ['Reviewer'],
    },
  ];
  readonly classes: MockClass[] = [
    restrictionClass('Privileged', 'Privileged', [
      'WorkspaceAdmin',
      'Reviewer',
      'QcReviewer',
      'PrivilegeReviewer',
      'ProductionManager',
      'Auditor',
    ]),
    restrictionClass('Confidential', 'Confidential', [
      'WorkspaceAdmin',
      'Reviewer',
      'QcReviewer',
      'PrivilegeReviewer',
      'ProductionManager',
      'Auditor',
    ]),
    restrictionClass('AttorneysEyesOnly', "Attorneys' Eyes Only", [
      'WorkspaceAdmin',
      'PrivilegeReviewer',
      'ProductionManager',
    ]),
  ];
  /** Every change received: `PUT …/role-assignments/…` and `PUT …/security/restriction-classes/…`. */
  readonly writes: { path: string; body: Json; ifMatch: string | null }[] = [];

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const m =
      /^\/api\/v1\/workspaces\/[^/]+\/(roles|role-assignments(?:\/.*)?|security\/restriction-classes(?:\/[^/]+)?)$/.exec(
        path,
      );
    if (!m) return undefined;
    const tail = m[1];
    if (tail === 'roles' && method === 'GET') {
      return route.fulfill({
        json: {
          roles: ROLES.map((r) => ({
            ...r,
            permissions: PERMISSIONS.filter(([, , roles]) => roles.includes(r.key)).map(
              ([name]) => name,
            ),
            usersOnly: r.key === BREAK_GLASS,
            needsInstallationAdmin: r.key === BREAK_GLASS,
          })),
          permissions: PERMISSIONS.map(([name, description]) => ({
            name,
            description,
            breakGlassEligible: ['Document.View', 'Search.Execute', 'Audit.Read'].includes(name),
          })),
        },
      });
    }
    if (tail === 'role-assignments' && method === 'GET') {
      return route.fulfill({ json: this.list(), headers: { ETag: `"${this.version}"` } });
    }
    if (tail === 'role-assignments/candidates' && method === 'GET') {
      const q = (url.searchParams.get('q') ?? '').toLowerCase();
      const people = [
        user('user-4', 'Morgan Reyes', 'morgan@example.test', []),
        user('user-5', 'Jordan Lee', 'jordan@example.test', []),
        ...this.principals.filter((p) => p.kind === 'user'),
      ];
      const items = [
        ...people.map(({ kind, userId, groupName, displayName, email }) => ({
          kind,
          userId,
          groupName,
          displayName,
          email,
        })),
        {
          kind: 'group',
          userId: null,
          groupName: 'cn=paralegals',
          displayName: 'cn=paralegals',
          email: null,
        },
      ]
        .filter((c, i, all) => all.findIndex((x) => x.displayName === c.displayName) === i)
        .filter(
          (c) => !q || c.displayName.toLowerCase().includes(q) || (c.email ?? '').includes(q),
        );
      return route.fulfill({ json: { items } });
    }
    const assign = /^role-assignments\/(users\/([^/]+)|groups)$/.exec(tail);
    if (assign && method === 'PUT') return this.replace(route, path, url, assign[2] ?? null);
    const cls = /^security\/restriction-classes(?:\/([^/]+))?$/.exec(tail);
    if (cls && !cls[1] && method === 'GET') return route.fulfill({ json: { items: this.classes } });
    if (cls?.[1] && method === 'PUT') return this.putClass(route, path, decodeURIComponent(cls[1]));
    return undefined;
  }

  private list() {
    return {
      items: [...this.principals]
        .sort((a, b) => a.displayName.localeCompare(b.displayName))
        .map((p) => this.resource(p)),
      version: this.version,
      administratorPaths: this.adminPaths(),
      canAssignBreakGlass: this.canAssignBreakGlass,
    };
  }

  private resource(p: MockPrincipal) {
    return {
      ...p,
      roles: ROLES.map((r) => r.key).filter((k) => p.roles.includes(k)),
      appliesToYou: appliesToMe(p),
    };
  }

  private adminPaths(): number {
    return this.principals.filter((p) => p.roles.includes(ADMIN)).length;
  }

  private replace(route: Route, path: string, url: URL, userId: string | null): Promise<void> {
    const request = route.request();
    const body = request.postDataJSON() as { roles: string[]; confirmSelfRemoval?: boolean };
    const ifMatch = request.headers()['if-match'] ?? null;
    this.writes.push({ path: path + url.search, body, ifMatch });
    if (!ifMatch)
      return route.fulfill(problem(428, 'precondition-required', 'Precondition required'));
    if (ifMatch !== '*' && ifMatch !== `"${this.version}"`) {
      return route.fulfill(problem(412, 'version-conflict', 'Version conflict'));
    }
    const groupName = userId ? null : url.searchParams.get('name');
    let p = this.principals.find((x) => (userId ? x.userId === userId : x.groupName === groupName));
    if (!p) {
      const candidate = userId
        ? {
            'user-4': ['Morgan Reyes', 'morgan@example.test'],
            'user-5': ['Jordan Lee', 'jordan@example.test'],
          }[userId]
        : undefined;
      if (userId && !candidate) return route.fulfill(problem(400, 'validation', 'Unknown user'));
      p = userId
        ? user(userId, candidate![0], candidate![1], [])
        : {
            kind: 'group',
            userId: null,
            groupName,
            displayName: groupName ?? '',
            email: null,
            roles: [],
          };
    }
    const added = body.roles.filter((r) => !p.roles.includes(r));
    const removed = p.roles.filter((r) => !body.roles.includes(r));
    if (added.length && appliesToMe(p))
      return route.fulfill(problem(403, 'self-protection', 'Self-protection'));
    if (added.includes(BREAK_GLASS) && (p.kind === 'group' || !this.canAssignBreakGlass)) {
      return route.fulfill(problem(403, 'forbidden', 'Forbidden'));
    }
    if (removed.includes(ADMIN) && this.adminPaths() <= 1) {
      return route.fulfill(problem(409, 'last-administrator', 'Last administrator'));
    }
    if (removed.length && appliesToMe(p) && !body.confirmSelfRemoval) {
      return route.fulfill(problem(409, 'confirmation-required', 'Confirmation required'));
    }
    if (added.length || removed.length) {
      p.roles = [...body.roles];
      if (!this.principals.includes(p)) this.principals.push(p);
      this.version += 1;
    }
    return route.fulfill({
      json: {
        principal: this.resource(p),
        version: this.version,
        administratorPaths: this.adminPaths(),
      },
      headers: { ETag: `"${this.version}"` },
    });
  }

  private putClass(route: Route, path: string, classKey: string): Promise<void> {
    const request = route.request();
    const body = request.postDataJSON() as { displayName: string; roles: string[]; rules: Json[] };
    const ifMatch = request.headers()['if-match'] ?? null;
    this.writes.push({ path, body, ifMatch });
    const c = this.classes.find((x) => x.classKey === classKey);
    if (!c) return route.fulfill(problem(404, 'not-found', 'Not found'));
    if (ifMatch !== `"${c.version}"`)
      return route.fulfill(problem(412, 'version-conflict', 'Version conflict'));
    const mine = new Set(this.principals.filter(appliesToMe).flatMap((p) => p.roles));
    const changed = [
      ...c.roles.filter((r) => !body.roles.includes(r)),
      ...body.roles.filter((r) => !c.roles.includes(r)),
    ];
    if (changed.some((r) => mine.has(r)))
      return route.fulfill(problem(403, 'self-protection', 'Self-protection'));
    c.roles = [...body.roles];
    c.version += 1;
    return route.fulfill({ json: c, headers: { ETag: `"${c.version}"` } });
  }
}

function user(userId: string, displayName: string, email: string, roles: string[]): MockPrincipal {
  return { kind: 'user', userId, groupName: null, displayName, email, roles };
}

function restrictionClass(classKey: string, displayName: string, roles: string[]): MockClass {
  return {
    classKey,
    displayName,
    isBuiltIn: true,
    roles,
    rules: [],
    updatedAt: '2026-10-01T09:00:00Z',
    version: 1,
  };
}

function appliesToMe(p: MockPrincipal): boolean {
  return p.kind === 'user' ? p.userId === ME : MY_GROUPS.includes(p.groupName ?? '');
}
