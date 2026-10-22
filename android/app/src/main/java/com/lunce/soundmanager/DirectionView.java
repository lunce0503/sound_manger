package com.lunce.soundmanager;

import android.content.Context;
import android.graphics.*;
import android.view.MotionEvent;
import android.view.View;
import java.util.function.IntConsumer;

final class DirectionView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private int azimuth;
    private final IntConsumer changed;

    public DirectionView(Context context) { this(context, 0, value -> { }); }

    DirectionView(Context context, int angle, IntConsumer changed) {
        super(context); azimuth = angle; this.changed = changed;
        setContentDescription("음원 방향. 위 앞쪽 0도, 오른쪽 90도, 아래 뒤쪽 180도, 왼쪽 270도");
        setFocusable(true);
    }

    void setAzimuth(int value) { azimuth = value; invalidate(); }

    @Override protected void onDraw(Canvas canvas) {
        float x = getWidth() / 2f, y = getHeight() / 2f, r = Math.min(x, y) - 30;
        paint.setColor(Color.rgb(54, 76, 104)); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(3);
        canvas.drawCircle(x, y, r, paint); canvas.drawLine(x-r, y, x+r, y, paint); canvas.drawLine(x, y-r, x, y+r, paint);
        paint.setStyle(Paint.Style.FILL); paint.setColor(Color.LTGRAY); paint.setTextSize(28); paint.setTextAlign(Paint.Align.CENTER);
        canvas.drawText("앞 0°", x, y-r-6, paint); canvas.drawText("뒤 180°", x, y+r+25, paint);
        canvas.drawCircle(x, y, 10, paint);
        double angle = Math.toRadians(azimuth);
        float px = x + (float)Math.sin(angle) * r, py = y - (float)Math.cos(angle) * r;
        paint.setColor(Color.rgb(110, 231, 208)); canvas.drawCircle(px, py, 15, paint);
    }

    @Override public boolean onTouchEvent(MotionEvent event) {
        if (event.getAction() == MotionEvent.ACTION_DOWN || event.getAction() == MotionEvent.ACTION_MOVE || event.getAction() == MotionEvent.ACTION_UP) {
            setPressed(event.getAction() != MotionEvent.ACTION_UP);
            getParent().requestDisallowInterceptTouchEvent(true);
            azimuth = ((int)Math.toDegrees(Math.atan2(event.getX()-getWidth()/2f, getHeight()/2f-event.getY())) + 360) % 360;
            invalidate();
            if (event.getAction() == MotionEvent.ACTION_UP) { performClick(); changed.accept(azimuth); }
            return true;
        }
        if (event.getAction() == MotionEvent.ACTION_CANCEL) { setPressed(false); return true; }
        return super.onTouchEvent(event);
    }
    @Override public boolean performClick() { super.performClick(); return true; }
}
