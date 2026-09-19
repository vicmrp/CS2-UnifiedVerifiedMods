'use strict';
// No Google script or request is made until the visitor opts in.
(() => {
  const id='G-8MNV3RFQL6', key='uvm-analytics-consent';
  const banner=document.querySelector('#analytics-consent');
  function stored(){try{return localStorage.getItem(key);}catch{return null;}}
  function remember(value){try{localStorage.setItem(key,value);}catch{}}
  let started=false;
  function start(){
    if(started)return;started=true;
    window['ga-disable-'+id]=false;
    window.dataLayer=window.dataLayer||[];
    window.gtag=function(){window.dataLayer.push(arguments);};
    gtag('consent','default',{analytics_storage:'granted',ad_storage:'denied',ad_user_data:'denied',ad_personalization:'denied'});
    gtag('js',new Date());
    gtag('config',id,{send_page_view:false,allow_google_signals:false,allow_ad_personalization_signals:false});
    // Never include search terms, selected files, hashes, query strings or fragments.
    gtag('event','page_view',{page_location:location.origin+location.pathname,page_title:'Unified Verified Mods',page_referrer:''});
    const script=document.createElement('script');script.async=true;script.src='https://www.googletagmanager.com/gtag/js?id='+id;document.head.append(script);
  }
  if(stored()==='yes')start();else if(stored()!=='no')banner.hidden=false;
  document.querySelector('#analytics-allow').addEventListener('click',()=>{remember('yes');banner.hidden=true;start();});
  document.querySelector('#analytics-reject').addEventListener('click',()=>{
    remember('no');banner.hidden=true;
    window['ga-disable-'+id]=true;
    for(const item of document.cookie.split(';')){const name=item.split('=')[0].trim();if(!/^_ga(?:_|$)/.test(name))continue;for(const domain of ['', ';domain='+location.hostname,';domain=.'+location.hostname])document.cookie=name+'=;max-age=0;path=/'+domain;}
    if(started)location.reload();
  });
  document.querySelector('#analytics-settings').addEventListener('click',()=>{banner.hidden=false;document.querySelector('#analytics-reject').focus();});
})();
