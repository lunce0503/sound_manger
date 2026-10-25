package com.lunce.soundmanager;

import android.app.Dialog;
import android.content.Context;
import android.view.*;
import android.widget.*;
import org.json.JSONObject;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.annotation.Config;
import org.robolectric.util.ReflectionHelpers;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=29)
public class InteractionTest {
    private Context context;
    private SecretKey key;
    private final List<ActivityController<MainActivity>> controllers=new ArrayList<>();
    @Before public void prepare() throws Exception {
        context=RuntimeEnvironment.getApplication(); context.getSharedPreferences("trusted_connection",Context.MODE_PRIVATE).edit().clear().commit();
        KeyGenerator generator=KeyGenerator.getInstance("AES"); generator.init(256); key=generator.generateKey();
    }
    @After public void cleanup() { for (ActivityController<MainActivity> controller:controllers) controller.pause().stop().destroy(); }
    private MainActivity activity() throws Exception {
        ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class);
        ReflectionHelpers.setField(controller.get(),"connectionStore",new ConnectionStore(context,key));
        controllers.add(controller); return controller.setup().get();
    }
    private void withDevice(MainActivity activity) throws Exception {
        activity.showState(new JSONObject("{\"settings\":{\"masterVolume\":0.7,\"bassDb\":0,\"trebleDb\":0,\"presenceDb\":0},\"sources\":[{\"id\":\"device1\",\"name\":\"테스트 휴대폰\",\"settings\":{\"volume\":1,\"muted\":false,\"spatial\":false,\"azimuth\":0}}]}"));
    }
    private List<View> all(View view) {
        List<View> values=new ArrayList<>(); values.add(view);
        if (view instanceof ViewGroup) for (int i=0;i<((ViewGroup)view).getChildCount();i++) values.addAll(all(((ViewGroup)view).getChildAt(i)));
        return values;
    }
    private View button(MainActivity activity,String text) {
        return all(activity.getWindow().getDecorView()).stream().filter(v->v instanceof Button && ((Button)v).getText().toString().contains(text)).findFirst().orElseThrow(AssertionError::new);
    }
    @Test public void homeHidesCredentialsEqSlidersAndDirectionSurface() throws Exception {
        MainActivity activity=activity(); withDevice(activity);
        List<View> views=all(activity.getWindow().getDecorView());
        assertFalse(views.stream().anyMatch(v->v instanceof EditText || v instanceof DirectionView));
        assertTrue(views.stream().anyMatch(v->v instanceof TextView && ((TextView)v).getText().toString().equals("전체 음량")));
        assertFalse(views.stream().anyMatch(v->v instanceof TextView && ((TextView)v).getText().toString().equals("저음")));
    }

    @Test public void reconnectedSourceCardShowsRestoredVolumePositionAndMute() throws Exception {
        MainActivity activity=activity(); withDevice(activity);
        activity.showState(new JSONObject("{\"settings\":{\"masterVolume\":0.7},\"sources\":[]}"));
        activity.showState(new JSONObject("{\"settings\":{\"masterVolume\":0.7},\"sources\":[{\"id\":\"new-session\",\"name\":\"테스트 휴대폰\",\"settings\":{\"volume\":0.35,\"muted\":true,\"spatial\":true,\"azimuth\":275}}]}"));
        List<View> views=all(activity.getWindow().getDecorView());
        Switch mute=(Switch)views.stream().filter(v->v instanceof Switch && ((Switch)v).getText().toString().equals("음소거")).findFirst().orElseThrow(AssertionError::new);
        assertTrue(mute.isChecked());
        SeekBar gain=(SeekBar)views.stream().filter(v->v instanceof SeekBar && "기기 음량".contentEquals(v.getContentDescription())).findFirst().orElseThrow(AssertionError::new);
        assertEquals(35,gain.getProgress());
        assertTrue(views.stream().anyMatch(v->v instanceof TextView && ((TextView)v).getText().toString().contains("275")));
    }
    @Test public void muteHasReadableLabelSpeakerIconAndToggleState() throws Exception {
        MainActivity activity=activity(); withDevice(activity);
        Switch mute=(Switch)all(activity.getWindow().getDecorView()).stream().filter(v->v instanceof Switch && ((Switch)v).getText().toString().equals("음소거")).findFirst().orElseThrow(AssertionError::new);
        assertFalse(mute.isChecked()); assertNotNull(mute.getCompoundDrawablesRelative()[0]);
        mute.performClick(); assertTrue(mute.isChecked()); assertNotNull(mute.getCompoundDrawablesRelative()[0]);
    }
    @Test public void directionAndEqualizerOpenInSeparateWindows() throws Exception {
        MainActivity activity=activity(); withDevice(activity);
        button(activity,"360° 방향 설정").performClick();
        Dialog direction=org.robolectric.shadows.ShadowDialog.getLatestDialog(); assertTrue(direction.isShowing());
        View surface=all(direction.getWindow().getDecorView()).stream().filter(v->v instanceof DirectionView).findFirst().orElseThrow(AssertionError::new);
        for (android.view.ViewParent parent=surface.getParent();parent!=null;parent=parent.getParent()) assertFalse(parent instanceof ScrollView);
        direction.dismiss(); button(activity,"이퀄라이저 설정").performClick();
        Dialog eq=org.robolectric.shadows.ShadowDialog.getLatestDialog(); assertTrue(eq.isShowing());
        assertTrue(all(eq.getWindow().getDecorView()).stream().anyMatch(v->v instanceof TextView && ((TextView)v).getText().toString().equals("저음")));
        eq.dismiss();
    }
    @Test public void scrollTouchOutsideHandleDoesNotClaimGestureOrChangeDirection() {
        AtomicInteger changes=new AtomicInteger(); LinearLayout parent=new LinearLayout(context);
        DirectionView view=new DirectionView(context,0,v->changes.incrementAndGet()); parent.addView(view); view.layout(0,0,300,300);
        MotionEvent down=MotionEvent.obtain(0,0,MotionEvent.ACTION_DOWN,150,150,0);
        assertFalse(view.onTouchEvent(down)); down.recycle(); assertFalse(view.isPressed()); assertEquals(0,changes.get());
    }
    @Test public void handleDragCommitsOnceAndCancelledGestureDoesNotCommit() {
        AtomicInteger changes=new AtomicInteger(); LinearLayout parent=new LinearLayout(context);
        DirectionView view=new DirectionView(context,0,v->changes.incrementAndGet()); parent.addView(view);
        int size=Ui.dp(context,300); view.layout(0,0,size,size);
        float x=size/2f,y=Ui.dp(context,36);
        MotionEvent down=MotionEvent.obtain(0,0,MotionEvent.ACTION_DOWN,x,y,0); assertTrue(view.onTouchEvent(down)); down.recycle();
        MotionEvent cancel=MotionEvent.obtain(0,10,MotionEvent.ACTION_CANCEL,x,y,0); view.onTouchEvent(cancel); cancel.recycle(); assertEquals(0,changes.get());
        down=MotionEvent.obtain(20,20,MotionEvent.ACTION_DOWN,x,y,0); assertTrue(view.onTouchEvent(down)); down.recycle();
        MotionEvent move=MotionEvent.obtain(20,30,MotionEvent.ACTION_MOVE,size-Ui.dp(context,36),size/2f,0); view.onTouchEvent(move); move.recycle();
        MotionEvent up=MotionEvent.obtain(20,40,MotionEvent.ACTION_UP,size-Ui.dp(context,36),size/2f,0); view.onTouchEvent(up); up.recycle(); assertEquals(1,changes.get());
    }
    @Test public void savedPairingSurvivesNewStoreAndIsOnlyEncryptedInPreferences() throws Exception {
        PairingLink pair=new PairingLink("https://example.trycloudflare.com","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ConnectionStore first=new ConnectionStore(context,key); first.save(pair);
        PairingLink restored=new ConnectionStore(context,key).load(); assertEquals(pair.address,restored.address); assertEquals(pair.key,restored.key);
        String stored=context.getSharedPreferences("trusted_connection",Context.MODE_PRIVATE).getAll().toString();
        assertFalse(stored.contains(pair.key)); assertFalse(stored.contains(pair.address));
        new ConnectionStore(context,key).clear(); assertNull(first.load());
    }
    @Test public void reopenedAppUsesSavedAuthenticationWithoutShowingPairingDialog() throws Exception {
        try (okhttp3.mockwebserver.MockWebServer server=new okhttp3.mockwebserver.MockWebServer()) {
            server.setDispatcher(new okhttp3.mockwebserver.Dispatcher() {
                @Override public okhttp3.mockwebserver.MockResponse dispatch(okhttp3.mockwebserver.RecordedRequest request) {
                    return new okhttp3.mockwebserver.MockResponse().setHeader("Content-Type","application/json")
                        .setBody("{\"settings\":{\"masterVolume\":0.7},\"sources\":[]}");
                }
            });
            server.start(java.net.InetAddress.getByName("127.0.0.1"),0);
            PairingLink pair=new PairingLink("http://127.0.0.1:"+server.getPort(),"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            new ConnectionStore(context,key).save(pair);
            MainActivity first=activity(); waitForConnected(first);
            ActivityController<MainActivity> old=controllers.remove(0); old.pause().stop().destroy();
            MainActivity reopened=activity(); waitForConnected(reopened);
            assertTrue(server.getRequestCount()>=2);
            assertEquals(pair.key,server.takeRequest(3,java.util.concurrent.TimeUnit.SECONDS).getHeader("X-Sound-Key"));
            assertFalse(all(reopened.getWindow().getDecorView()).stream().anyMatch(v->v instanceof EditText));
            assertNotNull(new ConnectionStore(context,key).load());
        }
    }
    private void waitForConnected(MainActivity activity) throws Exception {
        for (int i=0;i<150;i++) {
            Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
            if ((boolean)ReflectionHelpers.getField(activity,"connected")) return;
            Thread.sleep(20);
        }
        fail("Saved authentication did not reconnect");
    }
}
