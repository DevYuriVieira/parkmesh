const RECORD_SEPARATOR = "\u001e";

export class SignalRLiteClient {
  constructor(hubPath) {
    this.hubPath = hubPath;
    this.ws = null;
    this.handlers = new Map();
    this.pendingInvocations = new Map();
    this.nextInvocationId = 1;
    this.stateListeners = [];
    this.shouldReconnect = true;
  }

  on(eventName, callback) {
    if (!this.handlers.has(eventName)) {
      this.handlers.set(eventName, []);
    }
    this.handlers.get(eventName).push(callback);
  }

  onStateChange(callback) {
    this.stateListeners.push(callback);
  }

  emitState(state) {
    for (const cb of this.stateListeners) {
      cb(state);
    }
  }

  async connect() {
    this.shouldReconnect = true;
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const url = `${protocol}//${window.location.host}${this.hubPath}`;

    return new Promise((resolve, reject) => {
      let handshakeCompleted = false;
      const ws = new WebSocket(url);
      this.ws = ws;

      ws.onopen = () => {
        ws.send(JSON.stringify({ protocol: "json", version: 1 }) + RECORD_SEPARATOR);
      };

      ws.onmessage = (event) => {
        const frames = String(event.data).split(RECORD_SEPARATOR).filter(Boolean);
        for (const frame of frames) {
          const msg = JSON.parse(frame);

          if (!handshakeCompleted) {
            handshakeCompleted = true;
            if (msg.error) {
              this.emitState("error");
              reject(new Error(msg.error));
              return;
            }
            this.emitState("connected");
            resolve();
            continue;
          }

          if (msg.type === 1 && msg.target) {
            const list = this.handlers.get(msg.target) || [];
            for (const cb of list) {
              cb(...(msg.arguments || []));
            }
          } else if (msg.type === 3 && msg.invocationId) {
            const pending = this.pendingInvocations.get(msg.invocationId);
            if (pending) {
              this.pendingInvocations.delete(msg.invocationId);
              if (msg.error) {
                pending.reject(new Error(msg.error));
              } else {
                pending.resolve(msg.result);
              }
            }
          } else if (msg.type === 6) {
            ws.send(JSON.stringify({ type: 6 }) + RECORD_SEPARATOR);
          }
        }
      };

      ws.onerror = () => {
        if (!handshakeCompleted) {
          this.emitState("error");
          reject(new Error("SignalR WebSocket connection error"));
        }
      };

      ws.onclose = () => {
        this.emitState("disconnected");
        for (const pending of this.pendingInvocations.values()) {
          pending.reject(new Error("Connection closed"));
        }
        this.pendingInvocations.clear();

        if (this.shouldReconnect) {
          setTimeout(() => {
            this.connect().catch(() => {});
          }, 2000);
        }
      };
    });
  }

  invoke(target, ...args) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error("SignalR connection is not open"));
    }

    const invocationId = String(this.nextInvocationId++);
    return new Promise((resolve, reject) => {
      this.pendingInvocations.set(invocationId, { resolve, reject });
      const payload = {
        type: 1,
        invocationId,
        target,
        arguments: args
      };
      this.ws.send(JSON.stringify(payload) + RECORD_SEPARATOR);
    });
  }

  close() {
    this.shouldReconnect = false;
    if (this.ws) {
      this.ws.close();
    }
  }
}
