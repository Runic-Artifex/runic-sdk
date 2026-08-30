const resources = new Map();

export function createRunicDevtoolsObserver() {
  return { state() {}, trace() {} };
}

export function preserveRunicHmrResource(key, create) {
  if (resources.has(key)) return resources.get(key);
  const resource = create();
  resources.set(key, resource);
  return resource;
}

export async function disposeRunicHmrResource(key, dispose) {
  const resource = resources.get(key);
  if (resource === undefined) return;
  resources.delete(key);
  if (dispose) await dispose(resource);
}
