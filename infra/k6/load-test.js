import http from "k6/http";
import { check, sleep } from "k6";

export const options = {
  scenarios: {
    concurrent_arrivals: {
      executor: "constant-vus",
      vus: 16,
      duration: "30s",
    },
  },
  thresholds: {
    http_req_duration: ["p(95)<75", "p(99)<150"],
    http_req_failed: ["rate<0.05"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5150";
const API_KEY = __ENV.API_KEY || "";
const TENANTS = ["default", "garage-sp-01", "garage-rj-02"];
const GATES = ["GATE-N1", "GATE-S1"];
const TYPES = ["Standard", "EV", "SUV", "Pcd", "Moto"];

function headersFor(tenant) {
  const headers = {
    "Content-Type": "application/json",
    "X-Tenant-Id": tenant,
  };
  if (API_KEY) {
    headers["X-Api-Key"] = API_KEY;
  }
  return headers;
}

export default function () {
  const idx = __VU * 10000 + __ITER;
  const tenant = TENANTS[idx % TENANTS.length];
  const gateId = GATES[idx % GATES.length];
  const vehicleType = TYPES[idx % TYPES.length];
  const sessionId = `K6-${__VU}-${__ITER}`;
  const headers = headersFor(tenant);

  const allocRes = http.post(
    `${BASE_URL}/api/parking/allocations`,
    JSON.stringify({ gateId, vehicleSessionId: sessionId, vehicleType, leaseSeconds: 60 }),
    { headers }
  );

  check(allocRes, {
    "alloc created or contended": (r) => r.status === 201 || r.status === 409,
  });

  if (allocRes.status !== 201) {
    sleep(0.05);
    return;
  }

  const body = allocRes.json();
  const bayId = body?.session?.assignedBayId;
  if (!bayId) {
    return;
  }

  const occRes = http.post(
    `${BASE_URL}/api/parking/bays/${bayId}/occupancy`,
    JSON.stringify({ occupied: true, occupantSessionId: sessionId }),
    { headers }
  );
  check(occRes, { "bay occupied 200": (r) => r.status === 200 });

  const vacRes = http.post(
    `${BASE_URL}/api/parking/bays/${bayId}/occupancy`,
    JSON.stringify({ occupied: false }),
    { headers }
  );
  check(vacRes, { "bay vacated 200": (r) => r.status === 200 });

  if (__ITER % 5 === 0) {
    const auditRes = http.get(`${BASE_URL}/api/parking/audit?limit=15`, { headers });
    check(auditRes, { "audit ok": (r) => r.status === 200 });
  }
}
