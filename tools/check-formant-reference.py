"""Numerical reference experiment for the cepstral STFT design; requires numpy.
NOT production code or a C# execution test. No recorded voices/files/network are used.
Run: python tools/check-formant-reference.py
"""
import numpy as np
N=1024;hop=256;fs=48000
n=np.arange(fs*2);f0=128
freq=np.arange(1,90)*f0
amps=(.01+np.exp(-.5*((freq-550)/110)**2)+.8*np.exp(-.5*((freq-1500)/140)**2)+.45*np.exp(-.5*((freq-2600)/190)**2))/(1+(freq/4500)**4)
x=np.sum(amps[:,None]*np.sin(2*np.pi*freq[:,None]*n/fs),axis=0);x*=.2/np.max(abs(x))
win=.5-.5*np.cos(2*np.pi*np.arange(N)/N);q=np.minimum(np.arange(N),N-np.arange(N));lifter=np.where(q<=48,1,np.where(q<96,.5+.5*np.cos(np.pi*(q-48)/48),0))
def process(p,f,on=True):
 r=2**(p/12);fr=2**(f/12);k=np.arange(513);dest=np.rint(k*r).astype(int);valid=(dest>=1)&(dest<512);step=2*np.pi*hop/N
 prev=np.zeros(513);phase=np.zeros(513);out=np.zeros(len(x)+N);voice=0
 for end in range(hop,len(x)+1,hop):
  frame=np.zeros(N);take=x[max(0,end-N):end];frame[-len(take):]=take
  sp=np.fft.rfft(frame*win);mag=abs(sp);ph=np.angle(sp);res=(ph-prev-k*step+np.pi)%(2*np.pi)-np.pi;prev=ph
  freqout=(k+res/step)*r
  if on:
   power=abs(sp)**2;logs=.5*np.log(np.maximum(1e-18,power));cep=np.fft.irfft(logs,n=N);env=np.fft.rfft(cep*lifter).real
   energy=np.sum(power[4:86]);flat=np.exp(np.mean(np.log(np.maximum(1e-18,power[4:86]))))/max(1e-18,energy/82)
   target=0 if energy<1e-8 else np.clip((.4-flat)/.3,0,1);voice+=.25*(target-voice)
   idx=np.clip(dest/fr,0,512);desired=np.interp(idx,k,env);band=np.clip((8000-dest*fs/N)/3000,0,1)*np.clip(dest*fs/N/150,0,1)
   mag*=np.exp(np.clip(desired-env,-np.log(8),np.log(8))*band*voice)
  mags=np.bincount(dest[valid],weights=mag[valid],minlength=513);sums=np.bincount(dest[valid],weights=(mag*freqout)[valid],minlength=513)
  inst=np.divide(sums,mags,out=k.astype(float),where=mags>1e-15);phase=(phase+inst*step+np.pi)%(2*np.pi)-np.pi
  synth=mags*np.exp(1j*phase);synth[[0,512]]=0
  out[end-1:end-1+N]+=np.fft.irfft(synth,n=N)*win*2/3
 return out
results={}
for p,f,on in [(0,0,True),(0,4,True),(0,-4,True),(4,0,True),(4,4,False)]:
 y=process(p,f,on)[fs:2*fs];spec=abs(np.fft.rfft(y*np.hanning(len(y))))**2; hz=np.fft.rfftfreq(len(y),1/fs)
 sel=(hz>=1100)&(hz<=2150);centroid=np.sum(spec[sel]*hz[sel])/np.sum(spec[sel])
 ac=np.fft.irfft(abs(np.fft.rfft(y,n=131072))**2);lag=round(fs/(f0*2**(p/12)));pick=np.argmax(ac[lag-5:lag+6])+lag-5
 results[p,f,on]=(fs/pick,centroid)
 print('pitch',p,'formant',f,'enabled',on, 'F0',round(fs/pick,2),'F2 band centroid',round(centroid,2),'rms',round(np.sqrt(np.mean(y*y)),4),'peak',round(max(abs(y)),3))

base=results[0,0,True][1]
assert abs(results[0,4,True][0]-128)<1
assert abs(results[0,-4,True][0]-128)<1
assert results[0,4,True][1]>base+150
assert results[0,-4,True][1]<base-100
assert abs(results[4,0,True][0]-128*2**(4/12))<1
assert abs(results[4,0,True][1]-base)<abs(results[4,4,False][1]-base)*.6
print("Reference DSP independence / envelope direction checks: PASS (not C# execution)")
