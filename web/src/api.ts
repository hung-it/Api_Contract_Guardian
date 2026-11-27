import { useEffect, useState } from "react";

export function onSessionExpired(listener: () => void) {
  window.addEventListener("guardian:unauthorized", listener);
  return () => window.removeEventListener("guardian:unauthorized", listener);
}

class RequestError extends Error {
  constructor(public status: number) {
    super(status === 401 ? "Sign in to view reports." :
      status === 429 ? "Too many sign-in attempts. Wait and try again." :
      status === 404 ? "This report is unavailable." : "Could not reach Guardian. Try again.");
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch("/api/v1" + path, init);
  if (!response.ok) {
    if (response.status === 401) window.dispatchEvent(new Event("guardian:unauthorized"));
    throw new RequestError(response.status);
  }
  return response.status === 204 ? undefined as T : await response.json() as T;
}

export async function authAction(path: "login" | "logout", credentials?: { email: string; password: string }) {
  const { token } = await request<{ token: string }>("/auth/csrf");
  try {
    await request("/auth/" + path, {
      method: "POST",
      headers: { "X-CSRF-TOKEN": token, "Content-Type": "application/json" },
      body: credentials ? JSON.stringify(credentials) : undefined,
    });
  } catch (error) {
    if (path === "login" && error instanceof RequestError && error.status === 401) {
      throw new Error("Email or password is incorrect.", { cause: error });
    }
    throw error;
  }
}

export async function verifyAnalysis(id: string) {
  const { token } = await request<{ token: string }>("/auth/csrf");
  try {
    await request("/analyses/" + id + "/verification", { method: "POST", headers: { "X-CSRF-TOKEN": token } });
  } catch (error) {
    if (error instanceof RequestError && error.status === 409) {
      throw new Error("The report could not be matched to trusted CI evidence.", { cause: error });
    }
    if (error instanceof RequestError && error.status === 503) {
      throw new Error("CI verification is unavailable. Try again later.", { cause: error });
    }
    throw error;
  }
}

type Resource<T> = { kind: "loading" } | { kind: "error"; message: string } | { kind: "ready"; value: T };

export function useResource<T>(path: string, generation = 0) {
  const [attempt, setAttempt] = useState(0);
  const key = JSON.stringify([path, attempt, generation]);
  const [result, setResult] = useState<{ key: string; resource: Resource<T> }>();
  useEffect(() => {
    const controller = new AbortController();
    void request<T>(path, { signal: controller.signal }).then(
      value => {
        if (!controller.signal.aborted) setResult({ key, resource: { kind: "ready", value } });
      },
      error => {
        if (!controller.signal.aborted) setResult({
          key, resource: { kind: "error", message: error instanceof Error ? error.message : "Could not load data." },
        });
      },
    );
    return () => controller.abort();
  }, [path, key]);
  const resource: Resource<T> = result?.key === key ? result.resource : { kind: "loading" };
  return { resource, retry: () => setAttempt(value => value + 1) };
}
