package com.lunce.soundmanager;

import android.content.Context;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import java.util.UUID;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=29)
public class DeviceIdentityTest {
    private Context context;
    @Before public void prepare() {
        context=RuntimeEnvironment.getApplication();
        context.getSharedPreferences("audio-device-identity",Context.MODE_PRIVATE).edit().clear().commit();
    }
    @Test public void captureRestartsAndAppContextsReuseThePersistedIdentity() {
        String id=DeviceIdentity.get(context);
        assertEquals(UUID.fromString(id).toString(),id);
        assertEquals(id,DeviceIdentity.get(context.getApplicationContext()));
        assertEquals(id,context.getSharedPreferences("audio-device-identity",Context.MODE_PRIVATE).getString("id",null));
    }
    @Test public void unrelatedPairingChangesDoNotReplaceIdentity() {
        String id=DeviceIdentity.get(context);
        context.getSharedPreferences("trusted_connection",Context.MODE_PRIVATE).edit().clear().commit();
        assertEquals(id,DeviceIdentity.get(context));
    }
    @Test public void clearedAppDataCreatesANewIdentity() {
        String old=DeviceIdentity.get(context);
        context.getSharedPreferences("audio-device-identity",Context.MODE_PRIVATE).edit().clear().commit();
        assertNotEquals(old,DeviceIdentity.get(context));
    }
    @Test public void damagedIdentityIsPreservedInsteadOfSilentlyResettingSettings() {
        context.getSharedPreferences("audio-device-identity",Context.MODE_PRIVATE).edit().putString("id","damaged").commit();
        assertThrows(IllegalArgumentException.class,()->DeviceIdentity.get(context));
        assertEquals("damaged",context.getSharedPreferences("audio-device-identity",Context.MODE_PRIVATE).getString("id",null));
    }
}
