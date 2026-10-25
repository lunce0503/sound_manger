package com.lunce.soundmanager;

import android.app.job.*;
import java.util.concurrent.*;

public final class UpdateJobService extends JobService {
    private ExecutorService worker;
    private volatile boolean stopped;
    @Override public boolean onStartJob(JobParameters parameters) {
        stopped=false;ExecutorService current=Executors.newSingleThreadExecutor();worker=current;
        current.execute(()->{try{AppUpdates.check(this,false);if(!stopped)jobFinished(parameters,false);}finally{current.shutdown();}});return true;
    }
    @Override public boolean onStopJob(JobParameters parameters) {stopped=true;if(worker!=null)worker.shutdownNow();return true;}
}
