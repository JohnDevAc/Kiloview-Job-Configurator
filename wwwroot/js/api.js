export async function api(url, options = {}) {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...options
  });
  return readResponse(response);
}

export async function upload(url, body) {
  const response = await fetch(url, { method: 'POST', body });
  return readResponse(response);
}

async function readResponse(response) {
  const text = await response.text();
  let data;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = { error: text };
  }
  if (!response.ok) {
    throw new Error(data?.error || data?.detail || `Request failed (${response.status})`);
  }
  return data;
}
