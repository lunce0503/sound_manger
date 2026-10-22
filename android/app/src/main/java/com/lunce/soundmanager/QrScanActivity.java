package com.lunce.soundmanager;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.view.WindowInsets;
import android.widget.Toast;
import com.google.zxing.BarcodeFormat;
import com.journeyapps.barcodescanner.*;
import java.util.Collections;

public final class QrScanActivity extends Activity {
    private DecoratedBarcodeView camera;
    private boolean completed;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        camera = new DecoratedBarcodeView(this);
        camera.getBarcodeView().setDecoderFactory(new DefaultDecoderFactory(Collections.singletonList(BarcodeFormat.QR_CODE)));
        camera.setStatusText(getString(R.string.scan_prompt));
        setContentView(camera);
        camera.setOnApplyWindowInsetsListener((view, insets) -> {
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars());
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            }
            return insets;
        });
        camera.decodeContinuous(new BarcodeCallback() {
            @Override public void barcodeResult(BarcodeResult result) {
                if (completed || result.getText() == null) return;
                completed = true; camera.pause();
                setResult(RESULT_OK, new Intent().putExtra("pairing", result.getText())); finish();
            }
            @Override public void possibleResultPoints(java.util.List<com.google.zxing.ResultPoint> points) { }
        });
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] { Manifest.permission.CAMERA }, 30);
    }

    @Override protected void onResume() {
        super.onResume();
        if (!completed && checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) camera.resume();
    }
    @Override protected void onPause() { camera.pause(); super.onPause(); }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(request, permissions, grants);
        if (request == 30 && grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED) camera.resume();
        else { Toast.makeText(this, R.string.camera_permission, Toast.LENGTH_LONG).show(); finish(); }
    }
}
