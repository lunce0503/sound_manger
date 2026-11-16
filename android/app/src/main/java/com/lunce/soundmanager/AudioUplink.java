package com.lunce.soundmanager;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.ByteString;
import org.json.JSONObject;

/** Keeps the capture session independent of a single WebSocket connection. */
final class AudioUplink implements AutoCloseable {
    private static final int MAX_QUEUED_BYTES = 38400;
    private static final String KEEPALIVE = "{\"type\":\"keepalive\"}";
    private final ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor();
    private final OkHttpClient client;
    private final Request request;
    private final String hello;
    private final Consumer<String> status;
    private final Runnable firstReady;
    private final Consumer<String> terminal;
    private WebSocket socket;
    private ScheduledFuture<?> pending;
    private boolean ready, startedCapture, closed;
    private int retrySeconds = 1;

    AudioUplink(OkHttpClient client, Request request, String hello, Consumer<String> status,
                Runnable firstReady, Consumer<String> terminal) {
        this.client = client;
        this.request = request;
        this.hello = hello;
        this.status = status;
        this.firstReady = firstReady;
        this.terminal = terminal;
    }

    synchronized void start() {
        if (closed || socket != null) return;
        status.accept("서버 연결 중…");
        socket = client.newWebSocket(request, new WebSocketListener() {
            @Override public void onOpen(WebSocket current, Response response) {
                sendHello(current);
            }
            @Override public void onMessage(WebSocket current, String text) {
                acknowledge(current, text);
            }
            @Override public void onFailure(WebSocket current, Throwable error, Response response) {
                boolean rejected = response != null && (response.code() == 401 || response.code() == 403);
                disconnected(current, rejected);
            }
            @Override public void onClosing(WebSocket current, int code, String reason) {
                disconnected(current, code == 1007 || code == 1008);
            }
        });
        WebSocket connecting = socket;
        pending = timer.schedule(() -> disconnected(connecting, false), 8, TimeUnit.SECONDS);
    }

    private synchronized void sendHello(WebSocket current) {
        if (current == socket && !current.send(hello)) disconnected(current, false);
    }

    private synchronized void acknowledge(WebSocket current, String text) {
        if (current != socket || ready) return;
        try {
            if (new JSONObject(text).getString("sourceId").isEmpty()) throw new IllegalArgumentException();
        } catch (Exception error) {
            disconnected(current, true);
            return;
        }
        cancelPending();
        ready = true;
        retrySeconds = 1;
        status.accept("원본 PCM 전송 중 · 48 kHz / 16-bit / stereo");
        pending = timer.scheduleWithFixedDelay(() -> heartbeat(current), 3, 3, TimeUnit.SECONDS);
        if (!startedCapture) {
            startedCapture = true;
            firstReady.run();
        }
    }

    private synchronized void heartbeat(WebSocket current) {
        if (current == socket && ready && !current.send(KEEPALIVE)) disconnected(current, false);
    }

    synchronized void send(byte[] pcm, int count) {
        if (!ready || socket == null) return; // Skip stale PCM while reconnecting.
        if (socket.queueSize() + count > MAX_QUEUED_BYTES || !socket.send(ByteString.of(pcm, 0, count))) {
            disconnected(socket, false);
        }
    }

    private synchronized void disconnected(WebSocket current, boolean rejected) {
        if (closed || current != socket) return;
        socket = null;
        ready = false;
        current.cancel();
        cancelPending();
        if (rejected) {
            close();
            terminal.accept("서버 인증·음원 형식·연결 한도를 확인하세요.");
            return;
        }
        status.accept("서버 재연결 대기 중…");
        pending = timer.schedule(this::start, retrySeconds, TimeUnit.SECONDS);
        retrySeconds = Math.min(retrySeconds * 2, 15);
    }

    private void cancelPending() {
        if (pending != null) pending.cancel(false);
        pending = null;
    }

    @Override public synchronized void close() {
        closed = true;
        ready = false;
        cancelPending();
        if (socket != null) socket.cancel();
        socket = null;
        timer.shutdownNow();
    }
}
