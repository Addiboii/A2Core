// JSON API client shared by both pages. The session token is passed in by the caller (the staff
// dashboard may keep it in browser storage when "keep me signed in" is chosen; never in URLs).
export class ApiError extends Error {
  constructor(public status: number, public code?: string) { super(code || String(status)); }
}

export async function api<T = unknown>(path: string, { method = 'GET', body, token }:
  { method?: string; body?: unknown; token?: string | null } = {}): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (token) headers.Authorization = 'Bearer ' + token;
  let response: Response;
  try {
    response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
  } catch {
    throw new ApiError(0);
  }
  const payload = response.headers.get('content-type')?.includes('json') ? await response.json() : null;
  if (!response.ok) throw new ApiError(response.status, response.status === 403 ? 'forbidden' : payload?.error);
  return payload as T;
}

/** An authenticated binary download (map images) as an object URL; null when it is missing. */
export async function apiBlobUrl(path: string, token?: string | null): Promise<string | null> {
  const response = await fetch(path, { headers: token ? { Authorization: 'Bearer ' + token } : {} }).catch(() => null);
  if (!response?.ok) return null;
  return URL.createObjectURL(await response.blob());
}

export function requestId(): string { return crypto.randomUUID(); }
