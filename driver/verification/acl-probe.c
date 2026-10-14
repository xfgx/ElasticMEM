#include <windows.h>
#include <stdio.h>
static HANDLE open_store(void){return CreateFileW(L"\\\\.\\ElasticMemStore",GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,NULL,OPEN_EXISTING,0,NULL);}
int main(void){HANDLE h,token=NULL,restricted=NULL;PSID admins=NULL;SID_IDENTIFIER_AUTHORITY authority=SECURITY_NT_AUTHORITY;SID_AND_ATTRIBUTES disabled;DWORD error;int rc=1;
 h=open_store();if(h==INVALID_HANDLE_VALUE){printf("admin_open_failed %lu\n",GetLastError());return 2;}CloseHandle(h);
 if(!OpenProcessToken(GetCurrentProcess(),TOKEN_DUPLICATE|TOKEN_QUERY,&token))goto done;
 if(!AllocateAndInitializeSid(&authority,2,SECURITY_BUILTIN_DOMAIN_RID,DOMAIN_ALIAS_RID_ADMINS,0,0,0,0,0,0,&admins))goto done;
 disabled.Sid=admins;disabled.Attributes=0;
 if(!CreateRestrictedToken(token,DISABLE_MAX_PRIVILEGE,1,&disabled,0,NULL,0,NULL,&restricted))goto done;
 if(!ImpersonateLoggedOnUser(restricted))goto done;
 SetLastError(0);h=open_store();error=GetLastError();RevertToSelf();
 if(h!=INVALID_HANDLE_VALUE){CloseHandle(h);printf("unexpected_restricted_access\n");goto done;}
 if(error!=ERROR_ACCESS_DENIED){printf("wrong_rejection %lu\n",error);goto done;}
 puts("{\"schema\":\"elasticmem.acl.restricted-token.v1\",\"status\":\"passed\",\"admin_open\":true,\"restricted_win32_error\":5,\"independent_standard_user_tested\":false}");rc=0;
 done:RevertToSelf();if(restricted)CloseHandle(restricted);if(token)CloseHandle(token);if(admins)FreeSid(admins);return rc;}
